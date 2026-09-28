CREATE UNIQUE INDEX topic_prompt_clan_text
  ON topic_prompt (clan_id, text);

INSERT INTO topic_prompt (clan_id, text)
SELECT c.clan_id, prompts.text
FROM clan_registry c
CROSS JOIN (
  VALUES
    ('Một điều thú vị bạn học được gần đây là gì?'),
    ('Nếu có thêm một ngày nghỉ, bạn sẽ dùng ngày đó như thế nào?'),
    ('Bạn đang nghe hoặc xem nội dung gì đáng giới thiệu?'),
    ('Một cải tiến nhỏ nào sẽ làm công việc hằng ngày dễ hơn?'),
    ('Món ăn nào bạn muốn giới thiệu cho cả clan?'),
    ('Mục tiêu nhỏ của bạn trong tuần này là gì?')
) AS prompts(text)
ON CONFLICT (clan_id, text) DO NOTHING;
