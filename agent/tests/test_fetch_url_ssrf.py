"""SSRF hardening of the `fetch_url` agent tool (docs/XULY_CTKT_REQUIREMENT_AUDIT.md §8).

`fetch_url` now goes through the shared boundary in aiq_agent.security.ssrf.
Everything here is network-free: DNS goes through a patched `resolve_host`,
HTTP through httpx.MockTransport, and the connection-pinning tests use the real
pinned transport over httpcore's mock network stream.
"""

from __future__ import annotations

import asyncio
import json

import httpcore
import httpx
import pytest

from aiq_agent.agentic import tools as agent_tools
from aiq_agent.agentic.tools import build_tools, fetch_public_url
from aiq_agent.security import ssrf
from aiq_agent.security.ssrf import (
    SsrfBlockedError,
    SsrfFetchError,
    safe_get,
    validate_outbound_url,
)

PUBLIC_V4 = "93.184.216.34"
PUBLIC_V6 = "2606:2800:220:1:248:1893:25c8:1946"


def _dns(monkeypatch: pytest.MonkeyPatch, mapping: dict[str, list[str]]) -> list[str]:
    """hostname -> list of A/AAAA answers. Returns the lookup log."""
    calls: list[str] = []

    def fake_resolve(host: str) -> list[str]:
        calls.append(host)
        if host not in mapping:
            raise OSError(f"no mock DNS entry for {host!r}")
        return list(mapping[host])

    monkeypatch.setattr(ssrf, "resolve_host", fake_resolve)
    return calls


def _no_dns(monkeypatch: pytest.MonkeyPatch) -> None:
    def fail(host: str) -> list[str]:
        raise AssertionError(f"DNS must not be consulted for {host!r}")

    monkeypatch.setattr(ssrf, "resolve_host", fail)


def _http(monkeypatch: pytest.MonkeyPatch, handler) -> list[httpx.Request]:
    """Route safe_get's client through a MockTransport; returns request log."""
    seen: list[httpx.Request] = []
    real_async_client = httpx.AsyncClient

    def recording(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return handler(request)

    transport = httpx.MockTransport(recording)

    def client_factory(*args, **kwargs):
        kwargs["transport"] = transport
        return real_async_client(*args, **kwargs)

    monkeypatch.setattr("aiq_agent.security.ssrf.httpx.AsyncClient", client_factory)
    return seen


def _no_http(monkeypatch: pytest.MonkeyPatch) -> list[httpx.Request]:
    def handler(request: httpx.Request) -> httpx.Response:
        raise AssertionError(f"no request may be sent, got {request.url}")

    return _http(monkeypatch, handler)


def _error(result: dict) -> dict:
    assert result.get("is_error") is True
    return json.loads(result["content"][0]["text"])


# --------------------------------------------------------------------------
# Allowed
# --------------------------------------------------------------------------


async def test_allows_public_hostname(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"example.com": [PUBLIC_V4, PUBLIC_V6]})
    target = await validate_outbound_url("https://example.com/page")
    assert target.addresses == (PUBLIC_V4, PUBLIC_V6)
    assert target.port == 443


async def test_fetch_url_returns_stripped_text(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"example.com": [PUBLIC_V4]})
    seen = _http(
        monkeypatch,
        lambda r: httpx.Response(
            200,
            headers={"content-type": "text/html; charset=utf-8"},
            content=b"<html><script>alert(1)</script><p>Hello <b>world</b></p></html>",
        ),
    )
    result = await fetch_public_url("https://example.com/")
    assert "is_error" not in result
    text = result["content"][0]["text"]
    assert "Hello world" in text and "alert" not in text
    # No credentials / cookies ever leave the agent.
    sent = seen[0].headers
    assert "authorization" not in sent and "cookie" not in sent
    assert sent["user-agent"].startswith("AIQ-Agent/")


async def test_fetch_url_tool_never_sends_user_jwt(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"example.com": [PUBLIC_V4]})
    seen = _http(monkeypatch, lambda r: httpx.Response(200, text="ok"))
    tools = {t.name: t for t in build_tools(token="secret-user-jwt", gateway_base="http://gateway")}
    await tools["fetch_url"].handler({"url": "https://example.com/"})
    assert len(seen) == 1
    assert all("secret-user-jwt" not in v for v in seen[0].headers.values())


# --------------------------------------------------------------------------
# Blocked before any network I/O: literal hosts / IPs
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("url", "code"),
    [
        ("http://localhost", "host_not_allowed"),
        ("http://localhost.", "host_not_allowed"),
        ("http://LOCALHOST:80/", "host_not_allowed"),
        ("http://api.localhost/", "host_not_allowed"),
        ("http://metadata.google.internal/computeMetadata/v1/", "host_not_allowed"),
        ("http://metadata.google.internal./", "host_not_allowed"),
        ("http://127.0.0.1", "address_not_public"),
        ("http://127.1", "address_not_public"),
        ("http://2130706433/", "address_not_public"),  # decimal 127.0.0.1
        ("http://0x7f.1/", "address_not_public"),
        ("http://0.0.0.0", "address_not_public"),
        ("http://10.0.0.1", "address_not_public"),
        ("http://172.16.0.1", "address_not_public"),
        ("http://192.168.1.1", "address_not_public"),
        ("http://100.64.0.1", "address_not_public"),  # carrier-grade NAT
        ("http://169.254.169.254/latest/meta-data/", "address_not_public"),
        ("http://224.0.0.1", "address_not_public"),
        ("http://255.255.255.255", "address_not_public"),
        ("http://[::1]", "address_not_public"),
        ("http://[::]", "address_not_public"),
        ("http://[fe80::1]", "address_not_public"),
        ("http://[fc00::1]", "address_not_public"),
        ("http://[fd12:3456::1]", "address_not_public"),
        ("http://[ff02::1]", "address_not_public"),
        ("http://[ff0e::1]", "address_not_public"),
        ("http://[::ffff:127.0.0.1]", "address_not_public"),
        ("http://[::ffff:169.254.169.254]", "address_not_public"),
        ("http://[::7f00:1]", "address_not_public"),  # IPv4-compatible loopback
        ("http://[64:ff9b::7f00:1]", "address_not_public"),  # NAT64 → 127.0.0.1
        ("http://[2002:a00:1::]", "address_not_public"),  # 6to4 → 10.0.0.1
    ],
)
async def test_blocks_internal_targets_before_request(
    url: str, code: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    _no_dns(monkeypatch)
    _no_http(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await safe_get(url)
    assert info.value.code == code
    # Resolved/internal IPs never appear in the model-facing message.
    assert "169.254.169.254" not in info.value.message or "169.254.169.254" in url


async def test_public_ip_literal_allowed() -> None:
    target = await validate_outbound_url(f"https://{PUBLIC_V4}/")
    assert target.addresses == (PUBLIC_V4,)
    target6 = await validate_outbound_url(f"https://[{PUBLIC_V6}]/")
    assert target6.addresses == (PUBLIC_V6,)


# --------------------------------------------------------------------------
# Scheme / credentials / malformed / ports
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "url",
    [
        "file:///etc/passwd",
        "ftp://example.com/x",
        "gopher://example.com:70/_x",
        "data:text/plain,hello",
        "javascript:alert(1)",
        "unix:///var/run/docker.sock",
        "smb://example.com/share",
    ],
)
async def test_blocks_unsupported_schemes(url: str, monkeypatch: pytest.MonkeyPatch) -> None:
    _no_dns(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url(url)
    assert info.value.code == "unsupported_scheme"


@pytest.mark.parametrize(
    "url",
    ["http://user:password@example.com/", "https://user@example.com/", "http://:pw@example.com/"],
)
async def test_blocks_embedded_credentials(url: str, monkeypatch: pytest.MonkeyPatch) -> None:
    _no_dns(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url(url)
    assert info.value.code == "credentials_not_allowed"


@pytest.mark.parametrize(
    "url", ["", "not a url", "http://", "http:///path", "http://[::1", "http://example.com:99999/"]
)
async def test_blocks_malformed_or_empty_host(url: str, monkeypatch: pytest.MonkeyPatch) -> None:
    _no_dns(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url(url)
    assert info.value.code == "invalid_url"


@pytest.mark.parametrize(
    "url", ["http://example.com:8000/", "https://example.com:6333/", "http://example.com:22/"]
)
async def test_blocks_non_web_ports(url: str, monkeypatch: pytest.MonkeyPatch) -> None:
    _no_dns(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url(url)
    assert info.value.code == "port_not_allowed"


async def test_explicit_default_ports_allowed(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"example.com": [PUBLIC_V4]})
    assert (await validate_outbound_url("http://example.com:80/")).port == 80
    assert (await validate_outbound_url("https://example.com:443/")).port == 443


async def test_any_port_mode_still_enforces_ip_policy(monkeypatch: pytest.MonkeyPatch) -> None:
    """import_excel_ledger keeps its any-port behaviour; IPs are still checked."""
    _dns(monkeypatch, {"example.com": [PUBLIC_V4]})
    assert (
        await validate_outbound_url("http://example.com:8443/", allowed_ports=None)
    ).port == 8443
    with pytest.raises(SsrfBlockedError, match="address_not_public"):
        await validate_outbound_url("http://127.0.0.1:8000/", allowed_ports=None)


# --------------------------------------------------------------------------
# DNS answers
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "answers",
    [
        ["10.0.0.5"],
        ["127.0.0.1"],
        ["169.254.169.254"],
        [PUBLIC_V4, "10.0.0.5"],  # mixed: ANY private answer blocks
        ["10.0.0.5", PUBLIC_V4],
        [PUBLIC_V6, "fd00::1"],
        ["fe80::1%eth0"],
        ["::1"],
        ["::ffff:192.168.1.1"],
    ],
)
async def test_blocks_hostname_with_any_non_public_answer(
    answers: list[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    _dns(monkeypatch, {"looks-public.example": answers})
    _no_http(monkeypatch)
    with pytest.raises(SsrfBlockedError) as info:
        await safe_get("https://looks-public.example/")
    assert info.value.code == "address_not_public"
    for ip in answers:
        assert ip.split("%")[0] not in info.value.message


async def test_unparseable_dns_answer_fails_closed(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"weird.example": ["not-an-ip"]})
    with pytest.raises(SsrfBlockedError, match="address_not_public"):
        await validate_outbound_url("https://weird.example/")


async def test_dns_failure_and_empty_answer(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"empty.example": []})
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url("https://missing.example/")
    assert info.value.code == "dns_resolution_failed"
    with pytest.raises(SsrfBlockedError) as info:
        await validate_outbound_url("https://empty.example/")
    assert info.value.code == "dns_resolution_failed"


async def test_metadata_hostnames_blocked_via_resolution(monkeypatch: pytest.MonkeyPatch) -> None:
    """Hostname blocklist is defence in depth; the IP check is authoritative."""
    _dns(monkeypatch, {"metadata.attacker.example": ["169.254.169.254"]})
    with pytest.raises(SsrfBlockedError, match="link-local"):
        await validate_outbound_url("http://metadata.attacker.example/")


async def test_dns_is_resolved_fresh_on_every_validation(monkeypatch: pytest.MonkeyPatch) -> None:
    answers = iter([[PUBLIC_V4], ["127.0.0.1"]])
    monkeypatch.setattr(ssrf, "resolve_host", lambda host: next(answers))
    await validate_outbound_url("https://rebind.example/")
    with pytest.raises(SsrfBlockedError, match="loopback"):
        await validate_outbound_url("https://rebind.example/")


# --------------------------------------------------------------------------
# Redirects
# --------------------------------------------------------------------------


async def test_redirect_public_to_public_allowed(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4], "b.example": [PUBLIC_V6]})

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "a.example":
            return httpx.Response(302, headers={"location": "https://b.example/final"})
        return httpx.Response(200, headers={"content-type": "text/plain"}, text="landed")

    seen = _http(monkeypatch, handler)
    result = await safe_get("https://a.example/start")
    assert result.content == b"landed"
    assert result.final_url == "https://b.example/final"
    assert [str(r.url) for r in seen] == ["https://a.example/start", "https://b.example/final"]


@pytest.mark.parametrize(
    "location",
    [
        "http://localhost:8000/",
        "http://127.0.0.1:8000/",
        "https://10.0.0.5/",
        "https://[::1]/",
        "https://[fd00::5]/",
        "http://169.254.169.254/latest/meta-data/",
        "https://metadata.google.internal/",
        "https://internal.example/",  # public-looking name, private answer
        "https://rebind.example/",  # first hop's own host, now rebound
        "file:///etc/passwd",
        "gopher://a.example/",
        "https://user:pw@a.example/",
        "https://a.example:8080/",
        "http://a.example/downgrade",  # https -> http
    ],
)
async def test_redirect_to_unsafe_target_blocked_before_second_request(
    location: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    rebind = iter([[PUBLIC_V4], ["127.0.0.1"]])

    def fake_resolve(host: str) -> list[str]:
        if host == "rebind.example":
            return next(rebind)
        return {"a.example": [PUBLIC_V4], "internal.example": ["10.9.9.9"]}[host]

    monkeypatch.setattr(ssrf, "resolve_host", fake_resolve)
    start = "https://rebind.example/start" if "rebind" in location else "https://a.example/start"

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(302, headers={"location": location})

    seen = _http(monkeypatch, handler)
    with pytest.raises(SsrfBlockedError) as info:
        await safe_get(start)
    assert info.value.code == "redirect_blocked"
    assert len(seen) == 1  # the unsafe hop was never requested


async def test_redirect_loop_and_limit(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})
    seen = _http(monkeypatch, lambda r: httpx.Response(302, headers={"location": "/again"}))
    with pytest.raises(SsrfBlockedError) as info:
        await safe_get("https://a.example/")
    assert info.value.code == "too_many_redirects"
    assert len(seen) == 4  # initial request + 3 redirects, never more


async def test_fetch_url_reports_redirect_block_without_internal_ip(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4], "internal.example": ["10.9.9.9"]})
    _http(
        monkeypatch,
        lambda r: httpx.Response(302, headers={"location": "https://internal.example/"}),
    )
    payload = _error(await fetch_public_url("https://a.example/"))
    assert payload["error"] == "redirect_blocked"
    assert "10.9.9.9" not in json.dumps(payload)


# --------------------------------------------------------------------------
# Size / timeout / content type / headers
# --------------------------------------------------------------------------


async def test_fetch_url_response_too_large_declared(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})
    _http(
        monkeypatch,
        lambda r: httpx.Response(
            200, headers={"content-length": str(50 * 1024 * 1024)}, content=b"x"
        ),
    )
    assert _error(await fetch_public_url("https://a.example/"))["error"] == "response_too_large"


async def test_fetch_url_response_too_large_streamed(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})
    produced = 0

    async def endless():
        nonlocal produced
        while True:
            produced += 65536
            yield b"x" * 65536

    _http(monkeypatch, lambda r: httpx.Response(200, content=endless()))
    assert _error(await fetch_public_url("https://a.example/"))["error"] == "response_too_large"
    assert produced <= agent_tools._FETCH_MAX_BYTES + 2 * 65536  # stopped reading at the cap


async def test_overall_deadline_bounds_slow_server(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})

    async def drip():
        while True:
            await asyncio.sleep(0.05)
            yield b"x"

    _http(monkeypatch, lambda r: httpx.Response(200, content=drip()))
    with pytest.raises(SsrfFetchError) as info:
        await safe_get("https://a.example/", timeout=0.3)
    assert info.value.code == "timeout"


async def test_fetch_url_timeout_is_structured(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})

    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("read timed out from 10.1.1.1", request=request)

    _http(monkeypatch, handler)
    payload = _error(await fetch_public_url("https://a.example/"))
    assert payload["error"] == "timeout"
    assert "10.1.1.1" not in payload["message"]


async def test_fetch_url_network_error_is_structured(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})

    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("[Errno 111] refused 10.1.1.1", request=request)

    _http(monkeypatch, handler)
    payload = _error(await fetch_public_url("https://a.example/"))
    assert payload["error"] == "request_failed"
    assert "10.1.1.1" not in payload["message"]


async def test_fetch_url_rejects_binary_content(monkeypatch: pytest.MonkeyPatch) -> None:
    _dns(monkeypatch, {"a.example": [PUBLIC_V4]})
    _http(
        monkeypatch,
        lambda r: httpx.Response(
            200, headers={"content-type": "application/octet-stream"}, content=b"\x7fELF"
        ),
    )
    payload = _error(await fetch_public_url("https://a.example/x.bin"))
    assert payload["error"] == "unsupported_content_type"


@pytest.mark.parametrize(
    ("url", "code"),
    [
        ("http://127.0.0.1:8000", "port_not_allowed"),
        ("http://127.0.0.1/", "address_not_public"),
        ("http://169.254.169.254", "address_not_public"),
        ("file:///etc/passwd", "unsupported_scheme"),
        ("http://user:password@example.com", "credentials_not_allowed"),
        ("", "invalid_url"),
    ],
)
async def test_fetch_url_tool_structured_blocks(
    url: str, code: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    _no_dns(monkeypatch)
    _no_http(monkeypatch)
    assert _error(await fetch_public_url(url))["error"] == code


async def test_safe_get_refuses_credential_headers() -> None:
    for header in ("Authorization", "cookie", "Proxy-Authorization"):
        with pytest.raises(ValueError):
            await safe_get(f"https://{PUBLIC_V4}/", headers={header: "x"})


# --------------------------------------------------------------------------
# Connection pinning (DNS rebinding) — real pinned transport, mock socket
# --------------------------------------------------------------------------


class _RecordingNetwork(httpcore.AsyncNetworkBackend):
    def __init__(self, body: bytes = b"pinned") -> None:
        self.connects: list[tuple[str, int]] = []
        self._body = body

    async def connect_tcp(
        self,
        host,
        port,
        timeout=None,  # noqa: ASYNC109 — httpcore backend interface
        local_address=None,
        socket_options=None,
    ):
        self.connects.append((host, port))
        return httpcore.AsyncMockStream(
            [
                b"HTTP/1.1 200 OK\r\n",
                b"Content-Type: text/plain\r\n",
                f"Content-Length: {len(self._body)}\r\n\r\n".encode(),
                self._body,
            ]
        )

    async def sleep(self, seconds: float) -> None:
        await asyncio.sleep(seconds)


async def test_connection_uses_validated_ip_not_a_second_lookup(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Rebinding: the resolver flips to 127.0.0.1 after validation. The socket
    must still go to the validated public IP, and DNS is consulted only once."""
    answers = iter([[PUBLIC_V4], ["127.0.0.1"]])
    lookups: list[str] = []

    def fake_resolve(host: str) -> list[str]:
        lookups.append(host)
        return next(answers)

    monkeypatch.setattr(ssrf, "resolve_host", fake_resolve)
    network = _RecordingNetwork()
    monkeypatch.setattr(ssrf.httpcore, "AnyIOBackend", lambda: network)

    result = await safe_get("http://rebind.example/")
    assert result.content == b"pinned"
    assert network.connects == [(PUBLIC_V4, 80)]
    assert lookups == ["rebind.example"]


async def test_pinned_backend_refuses_unvalidated_host() -> None:
    backend = ssrf._PinnedBackend()
    backend._inner = _RecordingNetwork()
    with pytest.raises(httpcore.ConnectError):
        await backend.connect_tcp("never-validated.example", 80)
    with pytest.raises(httpcore.ConnectError):
        await backend.connect_unix_socket("/var/run/docker.sock")
    assert backend._inner.connects == []
