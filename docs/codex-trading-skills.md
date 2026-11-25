# Codex skills cho BingX Trading Bot

Bốn skill nằm ở `.agents/skills/` tại gốc repo. Codex dò thư mục này từ thư mục làm việc lên gốc Git. Gõ `$` để chọn tên hoặc ghi trực tiếp tên skill trong yêu cầu. Nếu skill chưa xuất hiện sau khi cập nhật repo, mở phiên Codex mới trong repo. [Tài liệu Codex về vị trí và cách gọi skill](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills)

- [`$bingx-api-integration`](../.agents/skills/bingx-api-integration/SKILL.md) — “Kiểm tra vì sao endpoint income thành công nhưng parser dừng và viết test trên simulator.” Skill truy vết HTTP, mã BingX, dữ liệu đã parse, trạng thái lệnh và bảo vệ. Để kiểm thử luồng ghi lệnh, dùng khóa giả và mock loopback của repo.
- [`$quantitative-backtesting`](../.agents/skills/quantitative-backtesting/SKILL.md) — “So sánh Current và Experimental trên cùng dữ liệu, kiểm tra look-ahead và phân tích chi phí, drawdown, độ bất định ngoài mẫu.” Skill yêu cầu đầu vào có hash và phạm vi UTC, công bố mô hình khớp lệnh, các cửa sổ có chồng lấn và giới hạn cỡ mẫu.
- [`$trading-strategy-research`](../.agents/skills/trading-strategy-research/SKILL.md) — “Từ rejection funnel, đề xuất giả thuyết breakout/retest có thể bác bỏ và cách đánh giá nó.” Skill định nghĩa giả thuyết và tiêu chí trước khi thử. Muốn kết luận về hiệu quả, cần backtest và bằng chứng ngoài mẫu.
- [`$live-trading-safety`](../.agents/skills/live-trading-safety/SKILL.md) — “Kiểm tra bản build, risk limits, khả năng dừng entry và reconcile sau restart, rồi trả quyết định Go/No-Go.” Skill tách bằng chứng mô phỏng khỏi chứng cứ từ host và exchange. Bước kích hoạt vẫn đợi người vận hành duyệt bản triển khai cụ thể.

Quy trình nghiên cứu gắn mỗi lần chạy với phiên bản code, cấu hình và bộ dữ liệu:

1. Thu nến và metadata có manifest. Kiểm tra múi giờ, nến đã đóng, khoảng trống và warm-up.
2. Đọc Signal Diagnostics để phân biệt lượt đánh giá, ứng viên, yêu cầu đặt lệnh và fill. Chọn một giả thuyết có thể kiểm chứng, dựa trên dữ liệu và tài liệu chính thức.
3. Khóa cấu hình Current và Experimental. Replay cùng dữ liệu, chi phí, mô hình khớp và các giới hạn rủi ro. Lưu cả kết quả âm hoặc không có lệnh và mọi phiên bản đã thử.
4. Đánh giá theo các cửa sổ đi tới trước về thời gian. Ưu tiên dữ liệu chưa dùng để chọn mô hình, kiểm tra hiệu quả sau chi phí và độ phân tán giữa các regime, symbol. Đọc [tiêu chí Experimental](experimental-review-gates.md) trước khi kết luận.
5. Chạy Experimental trên Shadow/Paper có trạng thái riêng. So sánh với Current trong các khoảng cùng có dữ liệu. Ghi nhận gaps và chi phí quan sát được. [Cách đọc báo cáo Shadow](shadow-trading.md)
6. Duyệt độc lập hiệu quả, dữ liệu và an toàn vận hành. Kết quả ngắn hạn không đổi Current. Mỗi lần triển khai Live vẫn phải qua báo cáo, kiểm tra và phê duyệt.

Skill được viết tại chỗ bằng `$skill-creator`. Không cần `$skill-installer` cho skill đã nằm trong repo. Các gói chỉ chứa Markdown, không có scripts, package, dịch vụ mạng hay quyền API riêng. Khi cân nhắc skill bên thứ ba, kiểm tra publisher và phiên bản, đọc `SKILL.md` cùng toàn bộ scripts và dependencies, xem phạm vi truy cập mạng hoặc bí mật, rồi mới quyết định có cần cài. Tránh cài gói làm trùng vai trò hoặc yêu cầu quyền giao dịch.

Nguồn kỹ thuật và nghiên cứu được liên kết ngay tại skill áp dụng, gồm [BingX Futures API](https://bingx-api.github.io/docs/), [BingX perp orders](https://github.com/BingX-API/api-ai-skills/blob/main/skills/swap-trade/api-reference.md), [Time Series Momentum](https://doi.org/10.1016/j.jfineco.2011.11.003), [Lo và MacKinlay](https://www.nber.org/papers/w2977), [The Probability of Backtest Overfitting](https://escholarship.org/uc/item/4w1110bb) và [The Stationary Bootstrap](https://doi.org/10.1080/01621459.1994.10476870).
