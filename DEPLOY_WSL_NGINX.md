# Thực hành deploy .NET Web API với Ubuntu WSL, systemd và Nginx

Bài thực hành cho máy hiện tại: Ubuntu-24.04 trên WSL2, kiến trúc x86_64,
user `lamvv`. Máy đã có systemd, `/usr/bin/dotnet`, ASP.NET Core Runtime 8 và Nginx.
Nginx hiện có site ở port khác; bài này thêm site riêng ở 8088.

Ứng dụng .NET nghe tại `127.0.0.1:5080`; Nginx chuyển tiếp request từ
`127.0.0.1:8088`. systemd quản lý tiến trình .NET. Khi bật bot sau này,
BackgroundService chạy trong cùng tiến trình, không phụ thuộc số request HTTP.

Các lệnh cài service/reload bên dưới do bạn tự thực hiện. Cấu hình lab ban đầu
đặt `Bot__Enabled=false`, `Live__Enabled=false`: chỉ có HTTP, không khởi tạo market,
paper/live worker, không dùng BingX key và không ghi vào state hiện tại trên Windows.

## 1. Publish bản chạy trên Linux — PowerShell Windows

```powershell
Set-Location 'D:\referenced-chatgpt-conversation-this-is-an'
dotnet publish src/TradingBot.Worker/TradingBot.Worker.csproj -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o artifacts/wsl-nginx-lab
wsl -d Ubuntu-24.04
```

`-c Release` tạo bản deploy; `-r linux-x64` chọn thư viện native cho Linux;
`--self-contained false` dùng ASP.NET Core Runtime đã cài trong Ubuntu.
`UseAppHost=false` tạo ứng dụng chạy bằng `dotnet TradingBot.Worker.dll`.
Build không đưa `data/`, `exports/` và `appsettings.Development.json` vào publish.

Từ đây, các code block `bash` chạy trong terminal Ubuntu, không phải PowerShell.
Ổ `D:` của Windows được truy cập qua `/mnt/d` trong WSL.

## 2. Kiểm tra môi trường — Ubuntu

```bash
whoami
pwd
ps -p 1 -o comm=
dotnet --list-runtimes
systemctl is-active nginx
```

Kỳ vọng: user `lamvv`, PID 1 là `systemd`, có `Microsoft.AspNetCore.App 8.0.x`
và Nginx `active`. SDK 9 hiện có vẫn build được target net8.0; lab này chỉ cần
runtime vì bước publish đã thực hiện từ Windows.

Nếu thực hành trên Ubuntu mới, cài Nginx/runtime theo
[hướng dẫn .NET trên Ubuntu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install).
Máy hiện tại không cần cài lại. systemd đã bật trong `/etc/wsl.conf`, không cần
shutdown WSL để đổi cấu hình.

## 3. Copy bản publish vào thư mục ứng dụng

```bash
sudo install -d -m 0755 /opt/bingx-lab/app
sudo cp -r /mnt/d/referenced-chatgpt-conversation-this-is-an/artifacts/wsl-nginx-lab/. /opt/bingx-lab/app/
sudo install -d -o www-data -g www-data -m 0750 /var/lib/bingx-lab
ls /opt/bingx-lab/app
```

`sudo` chạy lệnh với quyền quản trị; `/opt/bingx-lab/app` chứa chương trình.
`/var/lib/bingx-lab` là nơi lưu dữ liệu riêng nếu bạn bật paper sau này.
Service chạy dưới user `www-data`, nên user này cần quyền ghi thư mục dữ liệu.

## 4. Cài file cấu hình

```bash
sudo install -m 0600 /mnt/d/referenced-chatgpt-conversation-this-is-an/deploy/wsl/bingx-lab.env.example /etc/bingx-lab.env
sudo nano /etc/bingx-lab.env
```

Giữ nguyên ở lần đầu:

```dotenv
ASPNETCORE_URLS=http://127.0.0.1:5080
Api__Key=local-api-smoke-key
Bot__Enabled=false
Live__Enabled=false
OpenAI__Enabled=false
Persistence__Provider=Sqlite
```

File mẫu còn đặt môi trường Production và đường dẫn state/database riêng trong
`/var/lib/bingx-lab`. Key ở đây là key minh họa dùng cho lab localhost.
Khi dùng trên VPS, thay bằng key riêng được sinh ngẫu nhiên và cấu hình HTTPS.

Trong Nano: `Ctrl+O`, Enter để lưu; `Ctrl+X` để thoát. Quyền `0600` chỉ cho root
đọc/ghi file. systemd đọc file này rồi truyền biến môi trường cho ứng dụng.

Chạy .NET trực tiếp phải dùng các tên như `Api__Key`, `Bot__Enabled`,
`Live__Enabled`. Các tên `BOT_API_KEY`, `LIVE_ENABLED` trong ví dụ Docker trước
đây là biến được Compose ánh xạ, không phải tên cấu hình tương ứng của .NET.

## 5. Tạo systemd service để chạy nền

```bash
sudo install -m 0644 /mnt/d/referenced-chatgpt-conversation-this-is-an/deploy/wsl/bingx-lab.service /etc/systemd/system/bingx-lab.service
sudo systemctl daemon-reload
sudo systemctl enable --now bingx-lab
sudo systemctl status bingx-lab --no-pager
```

- `daemon-reload`: đọc lại định nghĩa service sau khi thêm/sửa file `.service`.
- `enable`: đăng ký khởi động khi systemd trong Ubuntu khởi động.
- `--now`: khởi động service ngay.
- `Restart=on-failure` trong file service: chạy lại nếu tiến trình bị lỗi.

Service đọc `/etc/bingx-lab.env`, chạy DLL trong `/opt/bingx-lab/app`. Cấu hình
ban đầu vẫn tắt worker; service này chỉ phục vụ HTTP.

Kiểm tra trực tiếp .NET trước khi thêm Nginx:

```bash
curl -i http://127.0.0.1:5080/health/live
curl -i http://127.0.0.1:5080/health/ready
```

Kỳ vọng: liveness **200 Healthy**, readiness **503 Unhealthy**. Readiness 503 là
đúng trong chế độ tắt bot, không phải lỗi cài đặt. Chỉ chuyển sang bước Nginx khi
liveness đã trả 200.

## 6. Thêm site Nginx

```bash
sudo install -m 0644 /mnt/d/referenced-chatgpt-conversation-this-is-an/deploy/wsl/nginx-bingx-lab.conf /etc/nginx/sites-available/bingx-lab
sudo ln -s /etc/nginx/sites-available/bingx-lab /etc/nginx/sites-enabled/bingx-lab
sudo nginx -t && sudo systemctl reload nginx
```

`sites-available` lưu cấu hình site. Symlink trong `sites-enabled` bật site đó.
Đây là cấu hình riêng; giữ nguyên các site Nginx hiện có.
`nginx -t` kiểm tra cú pháp; `&&` chỉ reload nếu kiểm tra thành công.
Nếu chạy lại bước tạo symlink và nhận `File exists`, kiểm tra link hiện có;
không cần tạo link lần nữa.

Kiểm tra qua Nginx:

```bash
curl -i http://127.0.0.1:8088/health/live
curl -i -H 'X-Api-Key: local-api-smoke-key' http://127.0.0.1:8088/api/bot/status
```

Kỳ vọng: cả hai trả 200; status có `enabled:false`, `liveTradingEnabled:false`,
`processing.initialized:false`. Không có header API key thì status trả 401.

## 7. Truy cập từ Windows

Giữ cửa sổ Ubuntu mở trong lúc thực hành. Trên trình duyệt Windows, mở:

- [Liveness qua Nginx](http://localhost:8088/health/live): hiển thị `Healthy`.
- [Readiness qua Nginx](http://localhost:8088/health/ready): Unhealthy/503 khi bot đang tắt.

WSL hỗ trợ truy cập ứng dụng Linux từ Windows qua localhost. Nếu localhost không
kết nối được nhưng curl trong Ubuntu thành công, kiểm tra ứng dụng Windows nào
đang chiếm port 8088 và cấu hình localhost forwarding của WSL.

Đây là Web API; đường dẫn `/` chưa có trang HTML và có thể trả 404. Để xem status,
dùng curl/Postman có header `X-Api-Key`.

## 8. Các lệnh vận hành cần nhớ

```bash
sudo systemctl status bingx-lab --no-pager
sudo journalctl -u bingx-lab -n 50 --no-pager
sudo journalctl -u bingx-lab -f
sudo systemctl stop bingx-lab
sudo systemctl start bingx-lab
sudo systemctl restart bingx-lab
```

Các dòng trên là các lệnh độc lập để bạn chọn dùng: không cần chạy cả block liên
tiếp. `Ctrl+C` thoát xem journal, không dừng service. Sửa `.env` cần restart service;
chỉ sửa file `.service` mới cần thêm `daemon-reload`.

Nếu gặp 502 từ Nginx, thử curl trực tiếp port 5080 và xem journal của service.
Nếu service báo missing framework, kiểm tra `dotnet --list-runtimes` có ASP.NET
Core 8. Nếu có 401, kiểm tra header API key; `/health/live` không yêu cầu key.

## 9. Chạy bot paper sau khi đã học xong luồng HTTP

Khi bạn muốn bật worker paper, sửa `/etc/bingx-lab.env`:

```dotenv
Bot__Enabled=true
Live__Enabled=false
OpenAI__Enabled=false
```

Sau đó chủ động chạy:

```bash
sudo systemctl restart bingx-lab
sudo journalctl -u bingx-lab -f
```

Lúc này bot dùng dữ liệu công khai BingX, mô phỏng giao dịch và lưu SQLite riêng
trong `/var/lib/bingx-lab`. Readiness cần warm-up/nến mới và dữ liệu đủ cho mọi
symbol; xem log nếu một symbol luôn thiếu lịch sử. Giữ live tắt trong bài lab.

## 10. Cập nhật bản lab

Publish bản mới từ PowerShell bằng lệnh ở bước 1. Khi muốn nạp bản mới trong Ubuntu:

```bash
sudo systemctl stop bingx-lab
sudo cp -r /mnt/d/referenced-chatgpt-conversation-this-is-an/artifacts/wsl-nginx-lab/. /opt/bingx-lab/app/
sudo systemctl start bingx-lab
curl -i http://127.0.0.1:8088/health/live
```

Giữ `/etc/bingx-lab.env` và `/var/lib/bingx-lab` qua các lần cập nhật. Khi lần đầu
cài lab, chỉ cài file env mẫu một lần; không copy đè env mẫu khi cập nhật.
Workflow CI/CD Docker hiện tại là phương án triển khai riêng, chưa cập nhật service
systemd này. Bài này tập trung deploy thủ công để bạn hiểu các bước trước.

WSL phụ thuộc Windows: Windows sleep/tắt hoặc WSL dừng thì ứng dụng cũng dừng.
systemd service không tự giữ WSL sống liên tục. Dùng WSL để học quy trình, sau đó
áp dụng trên VPS Ubuntu luôn hoạt động khi cần chạy bot liên tục.

Nguồn:

- [ASP.NET Core trên Linux với Nginx/systemd](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-8.0).
- [systemd trong WSL và giới hạn vòng đời](https://learn.microsoft.com/en-us/windows/wsl/systemd).
- [Truy cập ứng dụng WSL từ Windows](https://learn.microsoft.com/en-us/windows/wsl/networking).
