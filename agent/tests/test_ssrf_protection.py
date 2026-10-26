"""SSRF protection for aiq_agent.security.ssrf — used by import_excel_ledger.

Covers: scheme validation, localhost, loopback (v4/v6), private networks,
link-local (incl. cloud metadata), reserved/unspecified, DNS-resolution-based
bypass, redirect re-validation per hop, response size cap enforced
incrementally, and safe (non-leaking) error handling.
"""

from __future__ import annotations

import socket

import httpx
import pytest

from aiq_agent.security.ssrf import (
    SsrfBlockedError,
    SsrfFetchError,
    safe_get,
    validate_url,
)


def _fake_addrinfo(ip: str, family: int = socket.AF_INET):
    return [
        (family, socket.SOCK_STREAM, 6, "", (ip, 0) if family == socket.AF_INET else (ip, 0, 0, 0))
    ]


def _patch_transport(monkeypatch: pytest.MonkeyPatch, handler) -> None:
    """Route aiq_agent.security.ssrf's httpx.AsyncClient(...) calls through a
    MockTransport, without recursing into itself (must capture the REAL
    AsyncClient class before patching the shared httpx module attribute)."""
    real_async_client = httpx.AsyncClient
    transport = httpx.MockTransport(handler)

    def client_factory(*args, **kwargs):
        kwargs["transport"] = transport
        return real_async_client(*args, **kwargs)

    monkeypatch.setattr("aiq_agent.security.ssrf.httpx.AsyncClient", client_factory)


def _patch_dns(monkeypatch: pytest.MonkeyPatch, mapping: dict[str, str]) -> None:
    """Map hostname -> literal IP for socket.getaddrinfo, used by every test
    below instead of hitting real DNS (deterministic, network-free, and lets
    us give an innocuous-looking hostname a private-IP answer)."""

    def fake_getaddrinfo(host, *args, **kwargs):
        if host not in mapping:
            raise socket.gaierror(f"no mock DNS entry for {host!r}")
        ip = mapping[host]
        family = socket.AF_INET6 if ":" in ip else socket.AF_INET
        return _fake_addrinfo(ip, family)

    monkeypatch.setattr(socket, "getaddrinfo", fake_getaddrinfo)


# --------------------------------------------------------------------------
# Scheme / literal hostname
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "url", ["file:///etc/passwd", "ftp://example.test/x", "gopher://example.test/x"]
)
async def test_blocks_non_http_scheme(url: str) -> None:
    with pytest.raises(SsrfBlockedError, match="scheme"):
        await validate_url(url)


@pytest.mark.parametrize("host", ["localhost", "LOCALHOST", "localhost.localdomain"])
async def test_blocks_localhost_hostname(host: str, monkeypatch: pytest.MonkeyPatch) -> None:
    with pytest.raises(SsrfBlockedError, match="blocked"):
        await validate_url(f"http://{host}/x")


# --------------------------------------------------------------------------
# Loopback / private / link-local / reserved / unspecified — by literal IP
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    "ip",
    [
        "127.0.0.1",
        "127.0.0.53",
        "10.0.0.1",
        "172.16.5.9",
        "172.31.255.254",
        "192.168.1.1",
        "169.254.169.254",  # cloud metadata endpoint
        "169.254.1.1",
        "0.0.0.0",
    ],
)
async def test_blocks_ipv4_literal(ip: str) -> None:
    with pytest.raises(SsrfBlockedError):
        await validate_url(f"http://{ip}/x")


@pytest.mark.parametrize("ip", ["[::1]", "[fe80::1]", "[fc00::1]", "[::]"])
async def test_blocks_ipv6_literal(ip: str) -> None:
    with pytest.raises(SsrfBlockedError):
        await validate_url(f"http://{ip}/x")


async def test_blocks_ipv4_mapped_ipv6_loopback() -> None:
    with pytest.raises(SsrfBlockedError, match="ipv4-mapped"):
        await validate_url("http://[::ffff:127.0.0.1]/x")


async def test_allows_public_ip_literal() -> None:
    # 93.184.216.34 (example.com) is a real public unicast address — no DNS
    # needed since it's already a literal IP.
    hostname = await validate_url("https://93.184.216.34/x")
    assert hostname == "93.184.216.34"


# --------------------------------------------------------------------------
# DNS-resolution-based bypass: a benign-looking hostname resolving privately
# --------------------------------------------------------------------------


async def test_dns_resolution_blocks_hostname_resolving_to_private_ip(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(monkeypatch, {"looks-public.example.test": "10.1.2.3"})
    with pytest.raises(SsrfBlockedError, match="private"):
        await validate_url("http://looks-public.example.test/x")


async def test_dns_resolution_blocks_hostname_resolving_to_metadata_endpoint(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(monkeypatch, {"attacker-controlled.example.test": "169.254.169.254"})
    with pytest.raises(SsrfBlockedError, match="link-local"):
        await validate_url("http://attacker-controlled.example.test/x")


async def test_dns_resolution_failure_blocks(monkeypatch: pytest.MonkeyPatch) -> None:
    _patch_dns(monkeypatch, {})  # empty map -> every lookup raises gaierror
    with pytest.raises(SsrfBlockedError, match="DNS resolution failed"):
        await validate_url("http://nowhere.example.test/x")


async def test_dns_resolution_allows_hostname_resolving_publicly(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(monkeypatch, {"public.example.test": "93.184.216.34"})
    hostname = await validate_url("http://public.example.test/x")
    assert hostname == "public.example.test"


# --------------------------------------------------------------------------
# safe_get: redirect re-validation, size cap, error handling — via a mocked
# httpx transport (network-free) + mocked DNS.
# --------------------------------------------------------------------------


async def test_redirect_chain_revalidates_each_hop_and_blocks_private_target(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(
        monkeypatch,
        {
            "public-hop1.example.test": "93.184.216.34",
            # hop 2 looks like a normal public hostname string-wise, but
            # resolves privately — only DNS-resolution-based revalidation of
            # the REDIRECT TARGET (not just the original URL) catches this.
            "internal-hop2.example.test": "10.5.5.5",
        },
    )

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "public-hop1.example.test":
            return httpx.Response(302, headers={"location": "http://internal-hop2.example.test/x"})
        raise AssertionError(
            "must never reach the redirect target — blocked before the 2nd request"
        )

    _patch_transport(monkeypatch, handler)

    with pytest.raises(SsrfBlockedError, match="private"):
        await safe_get("http://public-hop1.example.test/start")


async def test_redirect_with_no_location_header_blocked(monkeypatch: pytest.MonkeyPatch) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(302)  # no Location header

    _patch_transport(monkeypatch, handler)
    with pytest.raises(SsrfBlockedError, match="Location"):
        await safe_get("http://host.example.test/x")


async def test_too_many_redirects_blocked(monkeypatch: pytest.MonkeyPatch) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(302, headers={"location": "http://host.example.test/next"})

    _patch_transport(monkeypatch, handler)
    with pytest.raises(SsrfBlockedError, match="too many redirects"):
        await safe_get("http://host.example.test/x", max_redirects=2)


async def test_response_size_cap_enforced_incrementally(monkeypatch: pytest.MonkeyPatch) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})
    big_body = b"x" * 2_000_000  # > 1 MB cap

    async def _achunks():
        for i in range(0, len(big_body), 8192):
            yield big_body[i : i + 8192]

    def handler(request: httpx.Request) -> httpx.Response:
        # An async byte-stream response has no Content-Length header —
        # exercises the *streamed* cap path (incremental aiter_bytes()
        # enforcement), not just the declared-header pre-check.
        return httpx.Response(200, content=_achunks())

    _patch_transport(monkeypatch, handler)
    with pytest.raises(SsrfBlockedError, match="exceeds"):
        await safe_get("http://host.example.test/x", max_bytes=1_048_576)


async def test_declared_content_length_over_cap_blocked_early(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, headers={"content-length": "5000000"}, content=b"x" * 100)

    _patch_transport(monkeypatch, handler)
    with pytest.raises(SsrfBlockedError, match="Content-Length"):
        await safe_get("http://host.example.test/x", max_bytes=1_048_576)


async def test_successful_fetch_returns_content(monkeypatch: pytest.MonkeyPatch) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, headers={"content-type": "text/csv"}, content=b"a,b,c\n1,2,3\n")

    _patch_transport(monkeypatch, handler)
    result = await safe_get("http://host.example.test/x")
    assert result.content == b"a,b,c\n1,2,3\n"
    assert result.content_type == "text/csv"
    assert result.status_code == 200


async def test_network_error_wrapped_as_fetch_error_not_leaked_raw(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _patch_dns(monkeypatch, {"host.example.test": "93.184.216.34"})

    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectTimeout("boom", request=request)

    _patch_transport(monkeypatch, handler)
    with pytest.raises(SsrfFetchError, match="ConnectTimeout"):
        await safe_get("http://host.example.test/x")


async def test_negative_max_redirects_rejected() -> None:
    with pytest.raises(ValueError):
        await safe_get("http://93.184.216.34/x", max_redirects=-1)
