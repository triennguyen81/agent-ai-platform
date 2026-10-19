"""SSRF-safe outbound HTTP fetch for tools that follow a caller-supplied URL.

This module is the single outbound-URL security boundary for the Agent.
Every tool that fetches a model/user-supplied URL (`fetch_url`,
`import_excel_ledger`, ...) must go through `safe_get()`; `validate_outbound_url()`
is the one validation function, run before the first request AND before every
redirect hop.

History: docs/ACCOUNTING_AGENT_AUDIT.md F-01 (import_excel_ledger) introduced
this module; docs/XULY_CTKT_REQUIREMENT_AUDIT.md §8 found `fetch_url` still
bypassing it (plain `httpx.AsyncClient(follow_redirects=True)`).

Policy
  - URL parsed with httpx's real URL parser (the same one used to send the
    request), never regex; malformed → `invalid_url`
  - scheme http/https only → `unsupported_scheme`
  - no userinfo (`http://user:pass@host`) → `credentials_not_allowed`
  - `localhost`, `*.localhost`, well-known metadata hostnames (trailing dot
    ignored) → `host_not_allowed` (defence in depth only — the IP check below
    is authoritative)
  - port allowlist (default 80/443) → `port_not_allowed`
  - every A/AAAA answer (or the literal IP, including inet_aton shorthand like
    `127.1` / `2130706433`) must be globally routable: `is_global`, and not
    loopback/link-local/private/reserved/multicast/unspecified; IPv4 embedded
    in IPv6 (v4-mapped, 6to4, Teredo, NAT64 64:ff9b::/96) is checked too.
    ANY non-public answer blocks the request → `address_not_public`
  - redirects are never auto-followed; each Location is joined, re-validated
    with the same function, and https→http downgrade is refused
    → `redirect_blocked` / `too_many_redirects`
  - Content-Length over the cap is rejected before reading; otherwise the cap
    is enforced while streaming → `response_too_large`
  - connect/read timeouts plus an overall deadline covering DNS, all hops and
    the body → `timeout`
  - `trust_env=False` (no env proxies, no .netrc credentials), cookies are not
    carried between hops, and callers may not pass auth/cookie headers

DNS rebinding: the addresses validated for a hop are pinned for that hop's
connection by `_PinnedBackend`, an httpcore network backend that connects only
to the pre-validated IPs. TLS still runs `start_tls(server_hostname=<original
host>)` inside httpcore, so certificate verification is unchanged — no custom
TLS handling. A host the validator never saw cannot be connected to at all.

Errors: `SsrfBlockedError` (policy) and `SsrfFetchError` (network/timeout) both
carry a stable `.code`; `str(exc)` is a short caller-safe message that never
contains resolved IP addresses or upstream exception text. `.detail` holds the
server-side detail and is logged, not returned to the model.
"""

from __future__ import annotations

import asyncio
import ipaddress
import logging
import socket
from collections.abc import Callable, Iterable
from dataclasses import dataclass

import httpcore
import httpx

logger = logging.getLogger(__name__)

ALLOWED_SCHEMES = frozenset({"http", "https"})
DEFAULT_ALLOWED_PORTS = frozenset({80, 443})
_DEFAULT_PORT = {"http": 80, "https": 443}
_BLOCKED_HOSTNAMES = frozenset(
    {
        "localhost",
        "localhost.localdomain",
        "ip6-localhost",
        "ip6-loopback",
        "metadata",
        "metadata.google.internal",
        "instance-data",
    }
)
_REDIRECT_STATUS = frozenset({301, 302, 303, 307, 308})
_FORBIDDEN_HEADERS = frozenset({"authorization", "proxy-authorization", "cookie"})
_NAT64 = ipaddress.ip_network("64:ff9b::/96")

IPAddress = ipaddress.IPv4Address | ipaddress.IPv6Address


class OutboundUrlError(Exception):
    """Base for every safe_get failure. `code` is stable and machine-readable;
    str(exc) is safe to show the model; `detail` is for server-side logs."""

    def __init__(self, code: str, message: str, detail: str | None = None) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.detail = detail or message


class SsrfBlockedError(OutboundUrlError):
    """The target URL (or a redirect hop) was blocked by SSRF policy."""


class SsrfFetchError(OutboundUrlError):
    """An ordinary network/timeout/protocol failure — not a policy block."""


@dataclass(frozen=True, slots=True)
class SafeFetchResult:
    content: bytes
    content_type: str
    final_url: str
    status_code: int


@dataclass(frozen=True, slots=True)
class ValidatedTarget:
    url: httpx.URL
    host: str  # ASCII (IDNA-encoded) host, exactly as httpcore will see it
    port: int
    addresses: tuple[str, ...]  # every resolved address, all verified public


def _non_public_reason(ip: IPAddress) -> str | None:
    """None if the address is publicly routable; a short reason otherwise."""
    if isinstance(ip, ipaddress.IPv6Address):
        embedded: list[tuple[str, ipaddress.IPv4Address]] = []
        if ip.ipv4_mapped is not None:
            embedded.append(("ipv4-mapped", ip.ipv4_mapped))
        if ip.sixtofour is not None:
            embedded.append(("6to4-embedded", ip.sixtofour))
        if ip.teredo is not None:
            embedded.append(("teredo-embedded", ip.teredo[1]))
        if ip in _NAT64:
            embedded.append(("nat64-embedded", ipaddress.IPv4Address(int(ip) & 0xFFFFFFFF)))
        for label, v4 in embedded:
            inner = _non_public_reason(v4)
            if inner is not None:
                return f"{label} {inner}"
    if ip.is_loopback:
        return "loopback"
    if ip.is_link_local:
        return "link-local"
    if ip.is_private:
        return "private"
    if ip.is_reserved:
        return "reserved"
    if ip.is_multicast:
        return "multicast"
    if ip.is_unspecified:
        return "unspecified"
    if not ip.is_global:
        return "non-global"
    return None


def _parse_ip_literal(host: str) -> IPAddress | None:
    """The host as an IP if it is any literal form the OS resolver would
    accept without DNS — including inet_aton shorthand (`127.1`, `0x7f.1`,
    `2130706433`) that `ipaddress` alone rejects."""
    try:
        return ipaddress.ip_address(host.split("%", 1)[0])
    except ValueError:
        pass
    if host and all(c in "0123456789abcdefxABCDEFX." for c in host):
        try:
            return ipaddress.IPv4Address(socket.inet_aton(host))
        except OSError:
            return None
    return None


def resolve_host(host: str) -> list[str]:
    """Blocking DNS resolution → every A/AAAA address. Tests replace this (or
    socket.getaddrinfo) to simulate public/private/mixed/rebinding answers."""
    infos = socket.getaddrinfo(host, None, type=socket.SOCK_STREAM)
    return [info[4][0] for info in infos]


def _validate_sync(
    url: str | httpx.URL, allowed_ports: frozenset[int] | None = DEFAULT_ALLOWED_PORTS
) -> ValidatedTarget:
    """The single validation function. Raises SsrfBlockedError on any policy
    violation. Blocking (DNS) — call through validate_outbound_url()."""
    try:
        parsed = url if isinstance(url, httpx.URL) else httpx.URL(str(url).strip())
    except (httpx.InvalidURL, TypeError, ValueError) as exc:
        raise SsrfBlockedError("invalid_url", "URL could not be parsed", str(exc)) from exc

    scheme = parsed.scheme.lower()
    if not scheme:
        raise SsrfBlockedError("invalid_url", "URL has no scheme")
    if scheme not in ALLOWED_SCHEMES:
        raise SsrfBlockedError(
            "unsupported_scheme", f"scheme {scheme!r} is not allowed (http/https only)"
        )
    if parsed.userinfo:
        raise SsrfBlockedError(
            "credentials_not_allowed", "URLs with embedded credentials are not allowed"
        )
    try:
        host = parsed.raw_host.decode("ascii").lower()
    except UnicodeDecodeError as exc:
        raise SsrfBlockedError("invalid_url", "URL host is not valid") from exc
    if not host:
        raise SsrfBlockedError("invalid_url", "URL has no hostname")

    bare_host = host.rstrip(".")
    if bare_host in _BLOCKED_HOSTNAMES or bare_host.endswith(".localhost"):
        raise SsrfBlockedError("host_not_allowed", f"host {bare_host!r} is blocked")

    port = parsed.port if parsed.port is not None else _DEFAULT_PORT[scheme]
    if not 1 <= port <= 65535:
        raise SsrfBlockedError("invalid_url", "URL port is out of range")
    if allowed_ports is not None and port not in allowed_ports:
        raise SsrfBlockedError(
            "port_not_allowed",
            f"port {port} is not allowed (allowed: {', '.join(map(str, sorted(allowed_ports)))})",
        )

    literal = _parse_ip_literal(host)
    if literal is not None:
        raw_ips = [str(literal)]
    else:
        try:
            raw_ips = resolve_host(host)
        except (OSError, UnicodeError) as exc:
            raise SsrfBlockedError(
                "dns_resolution_failed", f"DNS resolution failed for {bare_host!r}", str(exc)
            ) from exc
        if not raw_ips:
            raise SsrfBlockedError(
                "dns_resolution_failed", f"DNS resolution returned no addresses for {bare_host!r}"
            )

    addresses: list[str] = []
    for raw_ip in raw_ips:
        try:
            # IPv6 answers may carry a zone id ("fe80::1%eth0"); it does not
            # change the address class.
            ip = ipaddress.ip_address(raw_ip.split("%", 1)[0])
        except ValueError as exc:
            # Fail closed: an answer we cannot classify is not "safe".
            raise SsrfBlockedError(
                "address_not_public",
                f"host {bare_host!r} resolved to an unrecognised address",
                f"unparseable address {raw_ip!r} for {bare_host!r}",
            ) from exc
        reason = _non_public_reason(ip)
        if reason is not None:
            raise SsrfBlockedError(
                "address_not_public",
                f"host {bare_host!r} resolves to a non-public address ({reason})",
                f"resolved address {raw_ip} for {bare_host!r} is {reason}",
            )
        if str(ip) not in addresses:
            addresses.append(str(ip))
    return ValidatedTarget(url=parsed, host=host, port=port, addresses=tuple(addresses))


async def validate_outbound_url(
    url: str | httpx.URL, *, allowed_ports: frozenset[int] | None = DEFAULT_ALLOWED_PORTS
) -> ValidatedTarget:
    """Validate scheme, credentials, host, port and every DNS-resolved address
    for `url`. Raises SsrfBlockedError. Resolves fresh on every call — a
    previous decision is never reused."""
    return await asyncio.to_thread(_validate_sync, url, allowed_ports)


async def validate_url(url: str) -> str:
    """Back-compat wrapper (any port); returns the validated hostname."""
    target = await validate_outbound_url(url, allowed_ports=None)
    return target.url.host


class _PinnedBackend(httpcore.AsyncNetworkBackend):
    """httpcore network backend that only connects to addresses the SSRF
    validator approved for a host, closing the validate-then-resolve-again
    (DNS rebinding) window. TLS/SNI still use the original hostname."""

    def __init__(self) -> None:
        self._inner = httpcore.AnyIOBackend()
        self._pins: dict[str, tuple[str, ...]] = {}

    def pin(self, target: ValidatedTarget) -> None:
        self._pins[target.host] = target.addresses

    async def connect_tcp(
        self,
        host: str,
        port: int,
        timeout: float | None = None,  # noqa: ASYNC109 — httpcore backend interface
        local_address: str | None = None,
        socket_options: Iterable[httpcore.SOCKET_OPTION] | None = None,
    ) -> httpcore.AsyncNetworkStream:
        addresses = self._pins.get(host.lower())
        if not addresses:
            raise httpcore.ConnectError("host was not validated by the SSRF policy")
        last_exc: Exception | None = None
        for address in addresses:
            try:
                return await self._inner.connect_tcp(
                    address,
                    port,
                    timeout=timeout,
                    local_address=local_address,
                    socket_options=socket_options,
                )
            except (httpcore.ConnectError, httpcore.ConnectTimeout) as exc:
                last_exc = exc
        assert last_exc is not None
        raise last_exc

    async def connect_unix_socket(self, *args, **kwargs) -> httpcore.AsyncNetworkStream:
        raise httpcore.ConnectError("unix sockets are not allowed")

    async def sleep(self, seconds: float) -> None:
        await self._inner.sleep(seconds)


class _PinnedTransport(httpx.AsyncHTTPTransport):
    """Stock httpx transport whose connection pool uses _PinnedBackend.
    httpx 0.28 does not expose `network_backend`, so the pool is rebuilt with
    the same public httpcore API and httpx's own default SSL context."""

    def __init__(self, backend: _PinnedBackend) -> None:
        super().__init__(trust_env=False)
        self._pool = httpcore.AsyncConnectionPool(
            ssl_context=httpx.create_ssl_context(trust_env=False),
            network_backend=backend,
        )


async def safe_get(
    url: str,
    *,
    timeout: float = 30.0,  # noqa: ASYNC109 — overall deadline, enforced via asyncio.timeout
    max_bytes: int = 1_048_576,
    max_redirects: int = 3,
    headers: dict[str, str] | None = None,
    allowed_ports: frozenset[int] | None = DEFAULT_ALLOWED_PORTS,
    accept_content_type: Callable[[str], bool] | None = None,
) -> SafeFetchResult:
    """SSRF-hardened GET.

    `timeout` is the overall deadline for the whole call (DNS, every hop and
    the body); connect is additionally capped at 10 s. `allowed_ports=None`
    permits any port (the IP policy still applies). `accept_content_type`, if
    given, is checked before the body is read.

    Raises SsrfBlockedError for any policy violation, SsrfFetchError for a
    network/timeout failure. Never raises a raw httpx/socket exception.
    """
    if max_redirects < 0:
        raise ValueError("max_redirects must be >= 0")
    request_headers = dict(headers or {})
    if _FORBIDDEN_HEADERS & {name.lower() for name in request_headers}:
        raise ValueError("safe_get must not send credentials or cookies")

    try:
        async with asyncio.timeout(timeout):
            return await _safe_get_hops(
                url,
                timeout=timeout,
                max_bytes=max_bytes,
                max_redirects=max_redirects,
                headers=request_headers,
                allowed_ports=allowed_ports,
                accept_content_type=accept_content_type,
            )
    except TimeoutError as exc:
        raise SsrfFetchError("timeout", f"request exceeded the {timeout:g}s deadline") from exc


async def _safe_get_hops(
    url: str,
    *,
    timeout: float,  # noqa: ASYNC109 — per-operation httpx timeout
    max_bytes: int,
    max_redirects: int,
    headers: dict[str, str],
    allowed_ports: frozenset[int] | None,
    accept_content_type: Callable[[str], bool] | None,
) -> SafeFetchResult:
    backend = _PinnedBackend()
    target = await validate_outbound_url(url, allowed_ports=allowed_ports)

    async with httpx.AsyncClient(
        transport=_PinnedTransport(backend),
        timeout=httpx.Timeout(timeout, connect=min(timeout, 10.0)),
        follow_redirects=False,
        trust_env=False,
    ) as client:
        for hop in range(max_redirects + 1):
            backend.pin(target)
            client.cookies.clear()
            try:
                async with client.stream("GET", target.url, headers=headers) as response:
                    if response.status_code in _REDIRECT_STATUS:
                        location = response.headers.get("location")
                        if not location:
                            raise SsrfBlockedError(
                                "redirect_blocked",
                                f"redirect (status {response.status_code}) with no Location header",
                            )
                        if hop == max_redirects:
                            break
                        target = await _validate_redirect(target, location, allowed_ports)
                        continue

                    content_type = response.headers.get("content-type", "")
                    if accept_content_type is not None and not accept_content_type(content_type):
                        raise SsrfBlockedError(
                            "unsupported_content_type",
                            f"content type {content_type.split(';')[0].strip() or 'unknown'!r} "
                            "is not supported",
                        )
                    declared_length = response.headers.get("content-length")
                    if declared_length is not None:
                        try:
                            declared = int(declared_length)
                        except ValueError:
                            declared = None  # malformed — the streamed cap below still applies
                        if declared is not None and declared > max_bytes:
                            raise SsrfBlockedError(
                                "response_too_large",
                                f"declared Content-Length {declared} exceeds {max_bytes}-byte cap",
                            )

                    buffer = bytearray()
                    async for chunk in response.aiter_bytes():
                        buffer.extend(chunk)
                        if len(buffer) > max_bytes:
                            raise SsrfBlockedError(
                                "response_too_large",
                                f"response body exceeds {max_bytes}-byte cap while streaming",
                            )
                    return SafeFetchResult(
                        content=bytes(buffer),
                        content_type=content_type,
                        final_url=str(target.url),
                        status_code=response.status_code,
                    )
            except httpx.TimeoutException as exc:
                raise SsrfFetchError("timeout", exc.__class__.__name__, str(exc)) from exc
            except httpx.HTTPError as exc:
                raise SsrfFetchError("request_failed", exc.__class__.__name__, str(exc)) from exc
    raise SsrfBlockedError("too_many_redirects", f"too many redirects (> {max_redirects})")


async def _validate_redirect(
    current: ValidatedTarget, location: str, allowed_ports: frozenset[int] | None
) -> ValidatedTarget:
    """Join a Location against the current URL and run the full validator on
    it. Any rejection is reported as redirect_blocked (with the inner reason)."""
    try:
        next_url = current.url.join(location)
    except (httpx.InvalidURL, ValueError) as exc:
        raise SsrfBlockedError("redirect_blocked", "redirect target is not a valid URL") from exc
    if current.url.scheme == "https" and next_url.scheme == "http":
        raise SsrfBlockedError("redirect_blocked", "redirect from https to http is not allowed")
    try:
        return await validate_outbound_url(next_url, allowed_ports=allowed_ports)
    except SsrfBlockedError as exc:
        raise SsrfBlockedError(
            "redirect_blocked", f"redirect target rejected ({exc})", exc.detail
        ) from exc
