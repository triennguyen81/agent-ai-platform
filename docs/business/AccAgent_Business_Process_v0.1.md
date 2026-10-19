
AccAgent · Nhóm xử lý chứng từ
Quy Trình Nghiệp Vụ

Ba luồng xử lý chứng từ có trợ lý AI: hợp đồng, phiếu thu công nợ, hoá đơn thanh toán. Tài liệu này mô tả kế toán viên làm gì, agent làm gì, và điều gì tuyệt đối không thay đổi.
Phiên bản
v0.1 — bản thảo
Đối tượng
Kế toán viên, kế toán trưởng, trưởng bộ phận
Chế độ kế toán
Thông tư 200
Tài liệu song hành
Đặc Tả Kỹ Thuật AccAgent
Điều đầu tiên
Agent không ghi sổ

Mọi nghiệp vụ kế toán vẫn được thực hiện trên phần mềm kế toán, do kế toán viên thực hiện. Agent đứng ở hai đầu: chuẩn bị và kiểm tra dữ liệu trước khi nhập, soát xét và cảnh báo sau khi xuất. Nó không có quyền ghi vào sổ, không tự phát hành hoá đơn, không tự nộp tờ khai.
Agent làm	Người làm
Gom chứng từ từ mọi kênh về một chỗ, đặt đúng khách hàng và kỳ	Xử lý những chứng từ agent không phân loại được
Đọc số liệu trên chứng từ, kiểm tra tính hợp lệ, tra cứu cổng thuế	Soát lại những dòng agent báo độ tin cậy thấp
Đề xuất định khoản, đề xuất phương án ghép công nợ	Duyệt hoặc sửa đề xuất
Sinh file import theo mẫu phần mềm kế toán	Nhập vào phần mềm kế toán
Gửi thông báo và nhắc việc cho đúng người	Ra mọi quyết định có hệ quả tài chính hoặc pháp lý
Vì sao thiết kế như vậy

Trách nhiệm pháp lý về sổ sách thuộc về người ký, không thuộc về phần mềm. Giữ bước nhập liệu ở phía con người là cách duy nhất để trách nhiệm đó rõ ràng. Đổi lại, agent phải làm cho bước đó nhanh nhất có thể: người duyệt chỉ soát cái đáng ngờ, không gõ lại từ đầu.
Luồng 01
Hợp đồng

Hợp đồng là nguồn phát sinh nghĩa vụ. Từ hợp đồng sinh ra lịch thu tiền, mốc nghiệm thu và hạn theo dõi — những thứ mà phần mềm kế toán không quản lý, và hiện đang nằm rải rác trong file Excel của từng người.
Chứng từ vào

Hợp đồng bán hàng, mua hàng, thuê, vay · phụ lục · biên bản nghiệm thu. Nhận qua email, Zalo, ứng dụng nội bộ hoặc thư mục chia sẻ.
Bước 1Nhận & phân loạiXác định loại hợp đồng, truy phụ lục về hợp đồng gốc
Bước 2Đọc điều khoảnHai bên, giá trị, thanh toán, nghiệm thu, phạt, giữ lại
Bước 3Kiểm traMã số thuế đối tác, trùng hợp đồng, lệch mẫu chuẩn
Bước 4Sinh lịch thu tiềnTừ điều khoản thanh toán ra từng đợt và ngày dự kiến
Bước 5Kế toán công nợ soátĐối tác, giá trị, lịch thanh toán
Bước 6Bàn giaoMở mã đối tượng và mã hợp đồng trên phần mềm
Kết quả xử lý

Một hợp đồng đi qua luồng này cho ra sáu thứ cụ thể. Dưới đây là mẫu thật của một hợp đồng dịch vụ.
Kết quả 1 — Bản ghi hợp đồngHD-2026-0147
LoạiBán ra — Cung cấp dịch vụ
Số & ngày ký147/2026/HĐDV · 15/08/2026
Hiệu lực15/08/2026 → 31/12/2026
Bên muaCông ty CP ABC · MST 0301234567 đang hoạt động
Đại diện: Nguyễn Văn A — Giám đốc
Giá trị1.800.000.000 trước thuế · VAT 10% · 1.980.000.000 VNĐ
Thanh toán3 đợt · công nợ 30 ngày kể từ ngày hoá đơn
Nghiệm thuTheo từng giai đoạn, có biên bản hai bên
Phạt chậm0,05%/ngày
Tiền giữ lại5% đến hết bảo hành 12 tháng
Trạng tháiĐang hiệu lực · tiến độ thực hiện 0%
Kết quả 2 — Lịch thu tiền tự sinh3 đợt
Đợt	Mốc kích hoạt	Tỷ lệ	Số tiền	Ngày dự kiến	Trạng thái
1	Ký hợp đồng	30%	594.000.000	14/09/2026	Chờ thu
2	Nghiệm thu giai đoạn 1	40%	792.000.000	theo mốc	Chờ mốc
3	Nghiệm thu & bàn giao	30%	594.000.000	theo mốc	Chờ mốc
Kết quả 3 — Mốc theo dõi4 mốc
M1Bàn giao tài liệu thiết kế — hạn 30/09/2026
M2Nghiệm thu giai đoạn 1 — hạn 31/10/2026
M3Nghiệm thu tổng thể & bàn giao — hạn 15/12/2026
M4Hết hiệu lực hợp đồng 31/12/2026 — nhắc trước 30 ngày
Kết quả 4 — Cảnh báo cần người xử lý3 điểm
Lệch mẫuPhạt chậm thanh toán 0,05%/ngày, cao hơn mẫu chuẩn 0,03%
Thiếu hồ sơĐiều 3.2 dẫn chiếu phụ lục bảng giá nhưng chưa nhận được phụ lục
Điều khoản mởGiữ lại 5% chưa nêu điều kiện giải toả cụ thể

Kết quả 5 là đề xuất mở mã đối tượng công nợ mới cho đối tác chưa có trong danh mục. Kết quả 6 là gói bàn giao: mã đối tượng, mã hợp đồng, ba dòng lịch công nợ để kế toán mở trên phần mềm.
Ký hợp đồng không sinh bút toán

Luồng này kết thúc bằng dữ liệu quản trị, không có bút toán nào. Nghiệp vụ ghi sổ chỉ phát sinh khi nghiệm thu và xuất hoá đơn — xem bảng bút toán ở phần sau.
Luồng 02
Phiếu thu công nợ

Tiền về tài khoản là lúc dễ sai nhất: nội dung chuyển khoản viết tắt không dấu, khách trả gộp nhiều hoá đơn, trả thiếu, trả trước khi có hoá đơn. Luồng này ghép khoản tiền về đúng hoá đơn và đúng đợt hợp đồng, rồi đề xuất bút toán.
Chứng từ vào

Sao kê ngân hàng · giấy báo Có · uỷ nhiệm chi khách gửi · phiếu thu tiền mặt · biên lai ví điện tử.
Bước 1Đọc giao dịchNgày, số tiền, nội dung, người nộp
Bước 2Nhận diện kháchTừ nội dung chuyển khoản viết tắt
Bước 3Ghép công nợVới hoá đơn còn dư nợ và đợt hợp đồng
Bước 4Xác định tình huốngĐủ, một phần, thừa, trước hạn, bất định
Bước 5Kế toán xác nhậnChỉ soát dòng agent báo chưa chắc
Bước 6Bút toán hoặc việc tiếpGhi sổ, hoặc chuyển sang xử lý khác
Khi thanh toán thành công
Giao dịch & kết quả ghépGD-20260901-0034
Ngày · số tiền01/09/2026 · ghi Có 594.000.000 · VCB 0071000xxxxxx
Nội dung gốcCTY CP ABC TT DOT 1 HD 147/2026
Khách hàngCông ty CP ABC (KH-0892) — độ tin cậy 97%
Hợp đồngHD-2026-0147 · đợt 1
Hoá đơn00001234 ngày 16/08/2026 · 594.000.000
Kết luậnThu đủ — tất toán đợt 1 ✓
Nợ 1121 Tiền gửi ngân hàng VCB 594.000.000
    Có 1311 Phải thu KH-0892 / HĐ 00001234 594.000.000 Diễn giải: Thu tiền đợt 1 hợp đồng 147/2026 — Công ty CP ABC
Chứng từ gốc: sao kê VCB 01/09/2026 + uỷ nhiệm chi đính kèm

Kèm theo bút toán, hệ thống cập nhật: đóng đợt 1 trên lịch hợp đồng, tiến độ hợp đồng lên 30%, tuổi nợ của khách về 0, kích hoạt theo dõi mốc M2.
Khi thanh toán chưa thành công

Tám tình huống, mỗi tình huống một hướng xử lý dứt khoát. Không có tình huống nào để agent tự đoán rồi đi tiếp.
Tình huống	Bút toán	Hành động tiếp theo
Thu một phần
Về 400tr trên hoá đơn 594tr	Có, phần đã thu
Nợ 1121 / Có 1311	Đợt 1 giữ trạng thái thu một phần, còn 194tr. Đặt lịch nhắc. Tính lãi phạt nếu quá hạn theo hợp đồng.
Thu thừa
Về 600tr trên hoá đơn 594tr	Có
Nợ 1121 / Có 1311 594tr
Có 3388 6tr	Xác nhận với khách: hoàn trả hay cấn trừ đợt sau.
Thu trước khi có hoá đơn	Có, dạng trả trước
Nợ 1121 / Có 1311 chi tiết khách trả trước	Kích hoạt yêu cầu xuất hoá đơn cho kế toán doanh thu. Dùng 3387 nếu là doanh thu chưa thực hiện.
Không nhận diện được người nộp	Không	Vào hàng đợi tra soát. Gửi yêu cầu tra soát ngân hàng. Treo 3388 nếu quá 30 ngày.
Đúng khách nhưng không khớp hoá đơn nào	Không	Hỏi kinh doanh hoặc khách để xác định khoản thu thuộc về việc gì.
Giao dịch bị hoàn sau khi báo Có	Không	Đánh dấu huỷ, giữ nguyên công nợ, cảnh báo kế toán ngay.
Quá hạn không thu được	Không	Leo thang theo bậc: nhắc lần 1 → công văn → tính lãi phạt theo hợp đồng → chuyển pháp chế.
Thu bằng ngoại tệ	Bút toán chính
+ chênh lệch tỷ giá 515 hoặc 635	Áp tỷ giá theo quy định nội bộ, ghi rõ tỷ giá đã dùng.
Luồng 03
Hoá đơn thanh toán

Nhân viên nộp đề nghị thanh toán kèm một xấp hoá đơn chụp bằng điện thoại. Luồng này tách từng hoá đơn, đọc số liệu, xác thực trên cổng thuế, đối chiếu chính sách chi tiêu, rồi đề xuất bút toán và số tiền phải chi.
Chứng từ vào

Đề nghị thanh toán hoặc tạm ứng · hoá đơn GTGT · hoá đơn bán lẻ · vé điện tử · biên lai · bảng kê chi phí không hoá đơn · chứng từ quyết toán tạm ứng.
Bước 1Tách chứng từCắt từng hoá đơn khỏi ảnh chụp gộp
Bước 2Đọc số liệuNhà cung cấp, MST, số, ngày, tiền, thuế
Bước 3Xác thực cổng thuếHoá đơn có thật, còn hiệu lực, đúng bên mua
Bước 4Chống trùngĐã ai claim hoặc đã ghi sổ chưa
Bước 5Đối chiếu chính sáchĐịnh mức theo cấp bậc, loại chi hợp lệ
Bước 6Phân bổ & đối chiếu tạm ứngTài khoản, dự án, số còn phải trả
Bước 7Ba chặng duyệtBộ phận → kế toán → duyệt chi
Bước 8Bút toán & chi tiềnFile import + lệnh chi
Kết quả xử lý
Bảng kê quyết toán chi phíEC-2026-0451
Người nộpTrần Thị B — Phòng Kinh doanh — Chuyên viên
Mục đíchCông tác Hà Nội 20–22/08/2026 · Dự án PRJ-014
Đã tạm ứng5.000.000 — phiếu TU-2026-0088
#	Chứng từ	Nhà cung cấp	Tổng	TK	Kết quả
1	HĐ 0012345 — vé máy bay	Vietnam Airlines	3.500.000	6417	Đạt
2	HĐ 0007788 — khách sạn 2 đêm	KS Melia	3.000.000	6417	Cắt về 2.400.000
Định mức 1,5tr/đêm cấp chuyên viên
3	HĐ 0004411 — ăn uống	NH Sen Tây Hồ	880.000	6428	Đạt
4	Biên lai taxi, không hoá đơn	—	450.000	6417	Không được trừ
5	HĐ 0009900 — tiếp khách	NH Ngon	2.000.000	—	Chặn
Hoá đơn đã huỷ trên cổng thuế 25/08
Tổng đề nghị9.830.000
Trừ bị chặn− 2.000.000
Trừ cắt định mức− 600.000
Được duyệt7.230.000
Trừ tạm ứng− 5.000.000
Còn phải trả2.230.000
Thuế khấu trừ616.364 — dòng 1, 2, 3 (khách sạn tính trên mức đã cắt)
Không được trừ450.000 — taxi không hoá đơn, loại khi quyết toán TNDN
Nợ 6417 Chi phí công tác — PRJ-014 5.813.636
Nợ 6428 Chi phí ăn uống, tiếp khách 800.000
Nợ 1331 Thuế GTGT được khấu trừ 616.364
    Có 141 Tạm ứng — Trần Thị B 5.000.000
    Có 334 Phải trả người lao động 2.230.000 Diễn giải: Quyết toán công tác Hà Nội 20–22/08 — EC-2026-0451
Chứng từ gốc: 4 hoá đơn hợp lệ đính kèm, 1 hoá đơn bị chặn không tính
Ba kết cục có thể xảy ra
Kết cục	Khi nào	Việc tiếp theo
Duyệt đủ hoặc một phần	Không còn dòng bị chặn cần thay thế	Import bảng kê, lập lệnh chi phần còn phải trả, đóng phiếu tạm ứng, lưu chứng từ gốc gắn vào bút toán
Trả lại người nộp	Có dòng bị chặn cần hoá đơn thay thế, hoặc thiếu chứng từ gốc	Không sinh bút toán. Claim về trạng thái chờ bổ sung, kèm danh sách chính xác thiếu gì
Chờ duyệt ngoại lệ	Vượt định mức nhưng có lý do chính đáng	Treo chờ cấp có thẩm quyền quyết định, sau đó quay lại nhánh duyệt
Tra cứu nhanh
Bút toán đề xuất theo tình huống

Bảng này là danh mục đầy đủ những bút toán mà agent được phép đề xuất. Ngoài danh mục này, agent không tự sinh bút toán nào khác.
Nghiệp vụ	Nợ	Có	Ghi chú
Nghiệm thu mốc và xuất hoá đơn	131	511 · 3331	Kèm bút toán giá vốn 632 / 154·156
Điều chỉnh giảm doanh thu theo phụ lục	511 · 3331	131	Chỉ khi đã phát hành hoá đơn điều chỉnh
Thu tiền chuyển khoản	1121	131	Chi tiết khách hàng và hoá đơn
Thu tiền mặt	1111	131	—
Khách trả trước khi có hoá đơn	112	131 trả trước	3387 nếu là doanh thu chưa thực hiện
Phần khách trả thừa	112	3388	Tách dòng riêng, không gộp vào công nợ
Chênh lệch tỷ giá khi thu ngoại tệ	635	515	Theo chiều chênh lệch thực tế
Bù trừ công nợ hai chiều	331	131	Phải có biên bản bù trừ ký hai bên
Chi tạm ứng	141	1111 · 1121	Chi tiết người nhận, có hạn hoàn ứng
Quyết toán chi phí, đã tạm ứng	641·642·627 · 1331	141	Chênh lệch sang 334, hoặc thu hồi 1111
Quyết toán chi phí, không tạm ứng	641·642·627 · 1331	334 · 3388	Khi chi tiền: 334·3388 / 1111·1121
Hai cờ luôn đi kèm dòng chi phí

Mỗi dòng chi phí mang theo hai đánh dấu: thuế GTGT có được khấu trừ không, và chi phí có được trừ khi tính thuế thu nhập doanh nghiệp không. Chi phí không có hoá đơn vẫn được ghi nhận nhưng đánh dấu để loại khi quyết toán. Bỏ qua bước này thì đến kỳ quyết toán không ai lần lại được.
Trách nhiệm
Ai duyệt cái gì
Việc	Người quyết định	Người soát	Ngưỡng cần cấp cao hơn
Hồ sơ hợp đồng và lịch thu tiền	Kế toán công nợ	—	Kế toán trưởng nếu giá trị vượt ngưỡng công ty quy định
Điều khoản hợp đồng lệch mẫu chuẩn	Pháp chế	Kế toán trưởng	Ban giám đốc
Ghép khoản thu vào hoá đơn	Kế toán công nợ	—	Kế toán trưởng nếu khoản thu treo quá 30 ngày
Xử lý khoản thu thừa	Kế toán công nợ	Kế toán trưởng	—
Xoá nợ, ghi nhận nợ khó đòi	Ban giám đốc	Kế toán trưởng	Luôn
Nội dung claim hợp lý hay không	Trưởng bộ phận	—	—
Tính hợp lệ thuế và tài khoản hạch toán	Kế toán	—	Kế toán trưởng khi có dòng bị chặn
Duyệt chi	Theo hạn mức trong quy chế	Kế toán trưởng	Giám đốc trên hạn mức
Duyệt ngoại lệ vượt định mức	Cấp trên trực tiếp của người nộp	Kế toán trưởng	Giám đốc nếu vượt gấp đôi định mức
Nhập số liệu vào phần mềm kế toán	Luôn luôn là kế toán viên. Không bao giờ là agent.
Giao tiếp
Thông báo

Nguyên tắc: mỗi thông báo phải nói rõ việc gì và ai cần làm gì. Thông báo chỉ để "biết" mà không cần hành động thì gom vào bản tổng hợp cuối ngày, không gửi rời.
Người nhận	Khi nào	Nội dung
Kế toán công nợ	Đầu ngày	Giao dịch chờ xác nhận ghép · hợp đồng mới cần soát · khoản thu chưa xác định được nguồn
Kế toán công nợ	Ngay	Giao dịch bị hoàn sau khi đã báo Có
Kế toán doanh thu	Ngay	Có mốc nghiệm thu đủ điều kiện xuất hoá đơn · khách trả trước cần xuất hoá đơn
Kế toán thuế	Cuối tháng	Danh sách hoá đơn không hợp lệ · chi phí không được trừ khi quyết toán
Kế toán thanh toán	Theo đợt chi	Danh sách claim và tạm ứng đã duyệt cần chi tiền
Kế toán trưởng	Ngay	Claim trùng · hoá đơn đã huỷ · khoản thu treo quá hạn · chênh lệch đối chiếu vượt ngưỡng
Trưởng bộ phận	Ngay	Claim chờ duyệt · claim vượt định mức cần quyết định
Người nộp claim	Ngay sau xử lý	Dòng nào đạt, dòng nào bị chặn và lý do cụ thể, số được duyệt
Người nộp claim	Mỗi chặng duyệt	Đã duyệt · từ chối kèm lý do · đã chi tiền
Người tạm ứng	Trước hạn hoàn ứng	Nhắc trước n ngày, nhắc lại khi quá hạn
Kinh doanh, phụ trách hợp đồng	Khi có thay đổi	Khách đã thanh toán đợt X · khách trả thiếu còn bao nhiêu · mốc sắp đến hạn
Khách hàng	Sau khi kế toán duyệt	Xác nhận đã nhận tiền · số dư còn lại · hạn đợt tiếp theo
Ban giám đốc	Hằng tuần	Dòng tiền thu · tuổi nợ · nhóm khách quá hạn · chi phí theo bộ phận
Tuân thủ
Kiểm soát nội bộ

Ba luồng này chạm vào các điểm mà cơ quan thuế và kiểm toán hay soi. Những chốt kiểm soát dưới đây được cài sẵn, không phụ thuộc vào việc kế toán viên có nhớ hay không.
Chốt kiểm soát	Cơ sở	Cách hệ thống thực hiện
Hoá đơn phải tồn tại và còn hiệu lực	Quy định hoá đơn điện tử	Tra cổng thuế trước khi cho hoá đơn đi tiếp; hoá đơn đã huỷ hoặc thay thế bị chặn
Thông tin bên mua phải đúng doanh nghiệp	Điều kiện khấu trừ thuế GTGT	So tên, địa chỉ, mã số thuế trên hoá đơn với hồ sơ pháp nhân
Chi từ 20 triệu phải không dùng tiền mặt	Điều kiện khấu trừ và tính chi phí	Cảnh báo tự động khi phát hiện, đánh dấu không được khấu trừ
Không ghi nhận trùng chứng từ	Nguyên tắc kế toán	Đối chiếu với chứng từ đã claim và đã ghi sổ trước khi đề xuất
Chi phí không hoá đơn phải tách riêng	Quyết toán thuế TNDN	Gắn cờ không được trừ ngay từ lúc ghi nhận, không đợi cuối năm
Mọi bút toán truy được về chứng từ gốc	Yêu cầu kiểm toán	Mỗi dòng bút toán gắn với file chứng từ gốc bất biến
Người duyệt và người thực hiện tách bạch	Kiểm soát nội bộ	Người nộp claim không thể tự duyệt; người ghép công nợ không tự duyệt chi — **ĐÃ THAY THẾ cho ADMIN** bởi ADMIN Department Authority Invariant (docs/ACL_V2_RESOURCE_VISIBILITY_RULES.md §15): ADMIN cùng phòng ban được tự hoàn tất; chỉ USER không tự duyệt
Lưu vết mọi hiệu chỉnh	Yêu cầu kiểm toán	Mọi lần sửa đề xuất của agent đều ghi ai sửa, sửa gì, khi nào
Vận hành
Nhịp làm việc
Hằng ngày

    Đầu giờ: mở hàng đợi, xử lý giao dịch chờ ghép và chứng từ chờ phân loại.
    Trong ngày: claim và hợp đồng được xử lý theo dòng chảy, không dồn.
    Cuối giờ: nhập file import vào phần mềm kế toán cho các đề xuất đã duyệt trong ngày.

Hằng tuần

    Rà hàng đợi ngoại lệ, đảm bảo không có mục nào quá hạn xử lý.
    Soát các mốc hợp đồng sắp đến hạn trong 30 ngày tới.
    Gửi báo cáo dòng tiền và tuổi nợ cho ban giám đốc.

Hằng tháng

    Đối chiếu tổng thu trong kỳ giữa hệ thống, sao kê và sổ 131.
    Rà danh sách hoá đơn không hợp lệ và chi phí không được trừ, chuyển kế toán thuế.
    Rà các khoản treo phải trả khác, làm rõ trước khi khoá sổ.
    Xem lại các định mức chi tiêu bị vượt nhiều lần — dấu hiệu định mức đã lỗi thời chứ không phải nhân viên sai.

Đo lường
Chỉ số theo dõi
Chỉ số	Mục tiêu	Ý nghĩa
Tỷ lệ chứng từ đi thẳng không cần người sửa	> 70%	Thước đo trực tiếp lượng công việc thủ công đã bỏ được
Tỷ lệ khoản thu ghép đúng tự động	> 90%	Sau ba tháng học từ hiệu chỉnh của kế toán
Thời gian từ lúc nộp claim đến lúc nhận tiền	Giảm một nửa	Chỉ số nhân viên cảm nhận rõ nhất
Số hoá đơn không hợp lệ chặn được trước khi ghi sổ	Đo và báo cáo	Giá trị phòng ngừa, quan trọng hơn con số tiết kiệm giờ
Số khoản thu treo quá 30 ngày	Tiến về 0	Chất lượng quản lý công nợ
Số ngày đối chiếu công nợ cuối kỳ	Giảm 50%	Tác động lên tiến độ khoá sổ
Điều không đổi

Kế toán viên vẫn là người ghi sổ, vẫn là người ký, vẫn chịu trách nhiệm về số liệu. Cái thay đổi là họ không còn phải gõ lại số từ ảnh chụp, không còn phải dò sao kê tìm xem tiền của ai, và không còn phát hiện hoá đơn sai vào lúc đã quá muộn.
Quy Trình Nghiệp Vụ AccAgent · bản thảo v0.1 · dùng kèm Đặc Tả Kỹ Thuật AccAgent dành cho đội phát triển. Bảng bút toán và bảng phân quyền là phần phải được kế toán trưởng phê duyệt trước khi triển khai.
