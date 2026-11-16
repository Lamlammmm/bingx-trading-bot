# Deploy Ubuntu rồi bật CI/CD

Hướng dẫn này dùng Ubuntu 24.04 x86_64, tài khoản SSH có sudo (ví dụ `ubuntu`).
Thay `SERVER_IP` bằng IP/domain server của bạn. Workflow hiện build image trên
runner Ubuntu x86_64, nên VPS phải cùng kiến trúc (`uname -m` trả `x86_64`).

Source: [Lamlammmm/bingx-trading-bot](https://github.com/Lamlammmm/bingx-trading-bot).

Workflow có sẵn tại `.github/workflows/ci-cd.yml`, deploy script tại
`deploy/ubuntu/deploy.sh`. Bạn cần commit/push các file cùng phần Web API/strategy
hiện ở local trước khi clone bản mới trên server.

## 1. Đưa source lên GitHub

Trên máy phát triển, kiểm tra thay đổi rồi commit/push nhánh đang làm:

```powershell
git diff --stat
git status --short
git add .
git commit -m "Prepare bot Web API and Ubuntu deployment"
git push origin feature/multi-symbol-and-logging
```

CI chạy trên nhánh này nhưng chưa deploy. Sau khi cấu hình server và GitHub
environment bên dưới, tạo PR và merge vào `main` để kích hoạt CD.

## 2. Cài Docker trên Ubuntu

SSH vào VPS bằng tài khoản có sudo. Các lệnh bên dưới dành cho Ubuntu mới chưa
cài Docker; nếu đã cài Docker Engine/Compose, kiểm tra version rồi bỏ qua cài đặt.
Nguồn chính thức: [Docker Engine trên Ubuntu](https://docs.docker.com/engine/install/ubuntu/).

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git openssl python3 util-linux
sudo install -d -m 0755 /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

UBUNTU_RELEASE=$(. /etc/os-release; printf '%s' "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \
  "$(dpkg --print-architecture)" "$UBUNTU_RELEASE" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"
```

Thoát SSH rồi đăng nhập lại để nhận group `docker`. Kiểm tra:

```bash
docker version
docker compose version
docker run --rm hello-world
```

CI SSH bằng chính tài khoản này; nó cần gọi được Docker mà không nhập mật khẩu sudo.
Không cần cài .NET SDK trên VPS vì build/runtime nằm trong Docker image.

## 3. Chuẩn bị thư mục và cấu hình

Sau khi source mới đã được push ở bước 1:

```bash
sudo install -d -o "$USER" -g "$(id -gn)" /opt/bingx-trading-bot
git clone --branch feature/multi-symbol-and-logging \
  https://github.com/Lamlammmm/bingx-trading-bot.git /opt/bingx-trading-bot
cd /opt/bingx-trading-bot
cp .env.example .env
chmod 600 .env
openssl rand -hex 32
nano .env
```

Điền chuỗi vừa tạo vào `BOT_API_KEY`, giữ các mục sau lúc chạy thử:

```dotenv
BOT_API_KEY=chuoi-ngau-nhien-ban-vua-tao
COMPOSE_PROJECT_NAME=bingx-bot
BOT_HTTP_PORT=8080
BOT_ENABLED=true
LIVE_ENABLED=false
PERSISTENCE_PROVIDER=Sqlite
```

Đây là paper mode. `BOT_ENABLED=false` là chế độ chỉ mở HTTP, không khởi tạo bot.
Muốn dùng PostgreSQL hiện có, đặt `PERSISTENCE_PROVIDER=Postgres` và điền
`SUPABASE_DB_CONNECTION_STRING`. Khi bạn quyết định chạy live, đặt
`LIVE_ENABLED=true` và điền `BINGX_API_KEY`, `BINGX_API_SECRET` trong `.env` trên VPS.

`COMPOSE_PROJECT_NAME=bingx-bot` phải giữ ổn định: volume state là
`bingx-bot_bot-data`, được dùng chung giữa deploy thủ công và CD. Nếu đã chạy một
project Compose khác trước đó, chuyển dữ liệu sang volume này trước khi bật live.

Nếu tài khoản còn vị thế từ bot cũ, hãy dừng instance cũ, sao lưu và chuyển
`live-state.json`/database theo [DEPLOY_UBUNTU.md](DEPLOY_UBUNTU.md#chuyển-bot-đang-chạy-sang-server)
trước khi khởi động live trên VPS. Giữ cả pending order và journal outbox.

## 4. Chạy lần đầu trên Ubuntu

```bash
cd /opt/bingx-trading-bot
docker compose --project-name bingx-bot config --quiet
docker compose --project-name bingx-bot up -d --build trading-bot
docker compose --project-name bingx-bot ps
docker compose --project-name bingx-bot logs -f --tail=100 trading-bot
```

`Ctrl+C` thoát xem log; container vẫn chạy. `restart: unless-stopped` giúp container
chạy lại sau khi tiến trình lỗi hoặc VPS reboot, trừ khi bạn đã chủ động stop nó.

Trong một SSH session khác:

```bash
curl -i http://127.0.0.1:8080/health/live
curl -i http://127.0.0.1:8080/health/ready
read -rs -p 'Bot API key: ' BOT_STATUS_KEY; echo
curl -H "X-Api-Key: $BOT_STATUS_KEY" http://127.0.0.1:8080/api/bot/status
unset BOT_STATUS_KEY
```

Liveness trả 200 khi HTTP hoạt động. Readiness có thể trả 503 khi warm-up/chưa có
nến 5m mới hoặc một symbol thiếu dữ liệu; nó cần khởi tạo engine, xử lý thành công
và dữ liệu đủ mới cho mọi symbol. API-only mode luôn trả readiness 503.

Port 8080 chỉ bind localhost. Từ máy cá nhân, mở SSH tunnel:

```powershell
ssh -L 8080:127.0.0.1:8080 ubuntu@SERVER_IP
```

Sau đó gọi API ở localhost trên máy cá nhân với `X-Api-Key`.

## 5. Tạo SSH key riêng cho GitHub Actions

Trên máy Windows cá nhân, tạo key bên ngoài repository:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\.ssh" | Out-Null
ssh-keygen -t ed25519 -C "github-actions-bingx" -f "$env:USERPROFILE\.ssh\bingx_actions"
```

Khi hỏi passphrase, nhấn Enter hai lần để workflow dùng được key không tương tác.
Cài public key vào VPS và kiểm tra đăng nhập:

```powershell
Get-Content "$env:USERPROFILE\.ssh\bingx_actions.pub" | ssh ubuntu@SERVER_IP 'umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys'
ssh -i "$env:USERPROFILE\.ssh\bingx_actions" ubuntu@SERVER_IP "docker version"
```

Nếu SSH dùng port khác 22, thêm `-p PORT` vào hai lệnh SSH và đặt `DEPLOY_PORT`
ở bước tiếp theo.

Trên VPS đang đăng nhập qua kết nối tin cậy, lấy public host key:

```bash
cat /etc/ssh/ssh_host_ed25519_key.pub
```

Nó có dạng `ssh-ed25519 AAAAC3... comment`. Tạo nội dung known_hosts bằng cách
thêm IP/domain đúng như `DEPLOY_HOST` ở đầu (bỏ comment nếu muốn):

```text
SERVER_IP ssh-ed25519 AAAAC3...
```

Nếu dùng port 2222, dòng tương ứng là:

```text
[SERVER_IP]:2222 ssh-ed25519 AAAAC3...
```

## 6. Cấu hình GitHub environment

Vào repository → **Settings → Environments → New environment**, tên `production`.
Ở **Environment secrets**, thêm đúng bốn secret:

- `DEPLOY_HOST`: IP/domain VPS, không kèm `https://` hoặc port.
- `DEPLOY_USER`: tài khoản SSH dùng ở trên, ví dụ `ubuntu`.
- `DEPLOY_SSH_KEY`: toàn bộ nội dung private key `bingx_actions`, gồm cả dòng BEGIN/END.
- `DEPLOY_KNOWN_HOSTS`: dòng known_hosts đã tạo từ public host key của VPS.

Trong **Environment variables**, thêm `DEPLOY_PORT` nếu port SSH khác 22.
BingX/OpenAI/DB secrets vẫn nằm ở `/opt/bingx-trading-bot/.env`; workflow không
chép đè file này.

Nếu muốn tự chọn thời điểm restart mỗi lần deploy, bật **Required reviewers** và
chọn tài khoản của bạn. Với repo một người, để **Prevent self-review** tắt để bạn
có thể duyệt chính lần deploy mình kích hoạt. Giới hạn **Deployment branches**
cho `main`. Khi không bật Required reviewers, deploy tự chạy sau khi CI của `main` đạt.

Nguồn:

- [GitHub deployment environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments).
- [GitHub Actions secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets).

## 7. Chạy CI/CD và các lần cập nhật sau

Sau khi VPS có `.env` và GitHub có environment secrets, merge nhánh feature vào
`main`. Vào tab **Actions → Bot CI and Ubuntu deploy** để theo dõi.

Workflow hiện thực hiện:

1. Mọi push và PR vào `main`: test .NET, build image, chạy image với bot/live tắt,
   dùng collection Postman kiểm tra HTTP. Các bước này không đặt lệnh BingX.
2. Khi commit nằm trên `main`: đóng gói chính image đã kiểm tra thành artifact.
3. Job `deploy` dùng environment `production`, chờ reviewer nếu đã cấu hình.
4. Copy image/Compose/script vào `/opt/bingx-trading-bot/releases/<commit>-<run>-<attempt>`.
5. Load image mới, dừng container cũ, backup volume, tạo lại một container dùng
   cùng volume và `.env`, rồi kiểm tra health. Các lần deploy được tuần tự hóa.

Với `BOT_ENABLED=true`, script đợi cả readiness; vòng kiểm tra tối đa 120 lượt,
nghỉ 5 giây mỗi lượt. Nếu một symbol luôn thiếu dữ liệu, CD có thể báo thất bại
trong khi HTTP vẫn hoạt động. Xem logs để xử lý nguyên nhân.

Sau này chỉ cần commit → push feature → CI đạt → merge `main` → deploy (duyệt nếu
đã cấu hình). Bạn không cần git pull hay build thủ công trên VPS sau mỗi lần cập nhật.

Để bật live hoặc đổi cấu hình sau khi đã deploy bằng CD, sửa `.env` rồi chủ động
chạy lại script của release hiện tại:

```bash
cd /opt/bingx-trading-bot
nano .env
bash "$(cat current-release.txt)/deploy.sh"
```

Chạy lại script sẽ dừng/khởi động bot để nạp cấu hình. Với bản cài thủ công chưa có
`current-release.txt`, dùng `docker compose --project-name bingx-bot up -d trading-bot`
sau khi sửa `.env`.

## Khi deploy lỗi

```bash
cd /opt/bingx-trading-bot
docker compose --project-name bingx-bot ps -a
docker compose --project-name bingx-bot logs --tail=150 trading-bot
```

Backup file state/SQLite nằm trong `backups/`; PostgreSQL cần quy trình backup DB
riêng. Nếu bản mới đã khởi động nhưng health không đạt, script báo thất bại và giữ
nguyên state, không tự khôi phục backup: bot mới có thể đã đặt lệnh trên sàn.
Đối chiếu vị thế/pending order trước khi rollback hoặc sửa state. Không dùng
`docker compose down -v` nếu cần giữ dữ liệu.

## Phạm vi đã kiểm tra

Cú pháp Bash của deploy script và cấu hình Docker Compose đã được kiểm tra local.
Workflow cấu hình chạy test .NET và smoke HTTP trên release image. Chưa SSH vào
VPS hoặc chạy workflow thật trên GitHub trong bước soạn hướng dẫn này.
