#!/usr/bin/env bash
# pin_web_network.sh — Post-deploy hook for aiq-web.
#
# Docker Compose can't cleanly pin static IPs on aiq-net without recreating
# the whole network (which fails because sibling containers hold endpoints).
# Instead we:
#   1. Let compose bring the container up on its default (auto-assigned) IPs.
#   2. Move the container to the target static IPs by disconnect/connect.
#   3. Re-assert the iptables outbound-block rules against those IPs.
#
# Run this AFTER any of:
#   - docker compose up -d web
#   - docker restart aiq-web
#   - docker rm + docker compose up -d web
#
# The iptables DOCKER-USER rules survive Docker daemon restart via
# iptables-persistent (see /etc/iptables/rules.v4). This script only needs
# to reapply if the rules are missing (e.g. after re-imaging the host).

set -euo pipefail

CONTAINER=aiq-web
AIQ_NET=aiq-agentic_aiq-net
AGENTIC_NET=agentic-net
AIQ_IP=172.18.0.11
AGENTIC_IP=172.19.0.11

echo "[1/3] Reconnecting $CONTAINER to $AIQ_NET at $AIQ_IP..."
docker network disconnect $AIQ_NET $CONTAINER 2>/dev/null || true
docker network connect $AIQ_NET $CONTAINER --ip $AIQ_IP

echo "[2/3] Reconnecting $CONTAINER to $AGENTIC_NET at $AGENTIC_IP..."
docker network disconnect $AGENTIC_NET $CONTAINER 2>/dev/null || true
docker network connect $AGENTIC_NET $CONTAINER --ip $AGENTIC_IP

echo "[3/3] Ensuring iptables DOCKER-USER outbound-block rules present..."
for ip in $AIQ_IP $AGENTIC_IP; do
    if ! sudo iptables -C DOCKER-USER -s $ip ! -d 172.16.0.0/12 \
         -m conntrack --ctstate NEW -j REJECT --reject-with icmp-port-unreachable 2>/dev/null; then
        sudo iptables -I DOCKER-USER -s $ip ! -d 172.16.0.0/12 \
            -m conntrack --ctstate NEW -j REJECT --reject-with icmp-port-unreachable
        echo "  Added rule for $ip"
    else
        echo "  Rule for $ip already present"
    fi
done

sudo netfilter-persistent save >/dev/null 2>&1 || true

echo ""
echo "Verification:"
docker inspect $CONTAINER --format 'IPs: {{range $k, $v := .NetworkSettings.Networks}}{{$k}}={{$v.IPAddress}} {{end}}'
echo "Outbound test (should be REFUSED):"
docker exec $CONTAINER sh -c 'wget -qO- --timeout=3 https://icanhazip.com 2>&1 | tail -1 || echo "  Blocked (OK)"'
echo "Internal test (gateway should reply):"
docker exec $CONTAINER sh -c 'wget -qO- --timeout=3 http://aiq-gateway:8080/healthz/live 2>&1 | head -1 || echo "  BROKEN"'
