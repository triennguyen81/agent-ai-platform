# Tự động build và deploy (GitHub Actions → VPS)

Repo chạy workflow: **`triennguyen81/agent-ai-platform`**.

```
push master ──► CI (ci.yml: Gateway / Agent / Web test + build, compose validate)
                  │ xanh
                  ▼
           Build & Deploy (docker.yml)
             1. build 3 image → ghcr.io/<owner>/aiq-{gateway,agent,web}:sha-<7 ký tự> + :latest
             2. SSH vào VPS → scripts/deploy/remote-deploy.sh
                  sao lưu DB → pull image → recreate gateway/agent/web → health check
                  health check lỗi → tự quay về tag trước, workflow báo đỏ
```

- CI đỏ hoặc chạy cho pull request → **không** build, **không** deploy.
- Chỉ 3 dịch vụ app được thay; postgres, redis, qdrant, minio, caddy, monitoring giữ nguyên.
- Gateway tự chạy migration khi khởi động. **Rollback image không đảo migration** — mỗi lần deploy có bản `pg_dump` trong `backups/pre-deploy/` trên server (giữ 10 bản gần nhất).

## 1. Chuẩn bị VPS (một lần)

Yêu cầu: Linux có Docker Engine + Docker Compose v2, `curl`; cổng SSH mở cho GitHub Actions.

```bash
# user deploy riêng, thuộc nhóm docker
sudo adduser --disabled-password deploy && sudo usermod -aG docker deploy
sudo mkdir -p /opt/aiq && sudo chown deploy:deploy /opt/aiq

# thư mục chạy: lấy mã nguồn một lần để có infra/ (postgres init, caddy, prometheus, loki, grafana…)
sudo -u deploy git clone <repo> /opt/aiq
cd /opt/aiq && cp .env.example .env    # điền giá trị production (JWT_SIGNING_KEY, mật khẩu DB/Redis, API key…)
cp infra/caddy/Caddyfile.prod infra/caddy/Caddyfile   # thay aiq.example.com bằng domain thật
```

Khoá SSH cho GitHub Actions:

```bash
ssh-keygen -t ed25519 -N "" -C "github-actions-deploy" -f deploy_key
# trên VPS: thêm deploy_key.pub vào /home/deploy/.ssh/authorized_keys
ssh-keyscan -p 22 <ip-hoặc-domain-vps>        # kết quả → secret DEPLOY_KNOWN_HOSTS
```

Lần đầu khởi động toàn bộ hệ thống (script deploy chỉ thay 3 dịch vụ app):

```bash
cd /opt/aiq
echo <github-token-có-quyền-read:packages> | docker login ghcr.io -u <github-user> --password-stdin
AIQ_IMAGE_TAG=latest docker compose -p aiq-agentic -f docker-compose.yml -f docker-compose.prod.yml up -d --no-build
```

## 2. Cấu hình trên GitHub

**Settings → Environments → New environment `production`.** Nên bật *Required reviewers* nếu muốn duyệt tay trước mỗi lần deploy.

Secrets (của environment `production` hoặc repository):

| Secret | Giá trị |
|---|---|
| `DEPLOY_HOST` | IP hoặc domain VPS |
| `DEPLOY_USER` | `deploy` |
| `DEPLOY_SSH_KEY` | nội dung file `deploy_key` (private key) |
| `DEPLOY_KNOWN_HOSTS` | kết quả `ssh-keyscan` ở trên (bắt buộc — không tin host key mù) |

Variables (tuỳ chọn):

| Variable | Mặc định | Ý nghĩa |
|---|---|---|
| `DEPLOY_PATH` | `/opt/aiq` | thư mục chạy trên VPS |
| `DEPLOY_PORT` | `22` | cổng SSH |
| `COMPOSE_PROJECT_NAME` | `aiq-agentic` | tên project compose |
| `PRODUCTION_URL` | — | link hiển thị trên trang deployment |
| `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` | rỗng | chỉ khi dùng Clerk (được nhúng vào image web lúc build) |

Chưa có `DEPLOY_HOST`/`DEPLOY_USER` → workflow vẫn build image, bước deploy hiện cảnh báo và bỏ qua.

Image GHCR mặc định là private: server đăng nhập GHCR bằng token tạm của chính lần chạy workflow (quyền `packages: read`) rồi đăng xuất.

## 3. Vận hành

| Việc | Cách làm |
|---|---|
| Deploy thường | Merge / push vào `master`; theo dõi tab **Actions** → *CI* rồi *Build & Deploy* |
| Deploy lại nhánh hiện tại | Actions → *Build & Deploy* → **Run workflow**, để trống `image_tag` |
| Rollback | Actions → *Build & Deploy* → **Run workflow**, `image_tag` = tag cũ (ví dụ `sha-abc1234`; danh sách trong `/opt/aiq/.deploy/history` hoặc GitHub Packages). Không build lại |
| Chỉ build, không deploy | Run workflow, bỏ chọn `deploy` |
| Xem phiên bản đang chạy | `cat /opt/aiq/.deploy/current_tag` |
| Khôi phục DB sau migration lỗi | `docker compose -p aiq-agentic exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean < backups/pre-deploy/<file>.dump` (dừng gateway trước) |

Biến điều chỉnh script trên server (đặt trong môi trường khi chạy tay): `DEPLOY_HEALTH_TIMEOUT` (mặc định 180 giây), `DEPLOY_KEEP_BACKUPS` (10), `DEPLOY_BACKUP_DIR`.

Health check: `GET /healthz/ready` của Gateway (cổng `GATEWAY_PORT`, mặc định 8080), `GET /login` của Web (cổng `WEB_PORT`, 3030) và container Agent đang chạy — gọi qua `127.0.0.1` trên VPS.
