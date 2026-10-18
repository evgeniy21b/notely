# Notely — подсказки на созвоне (личный инструмент)

Слушает звук созвона (ScreenCaptureKit) и микрофон → Deepgram realtime (nova-3, ru) → если собеседник задал вопрос, OpenRouter (по умолчанию Claude Haiku 4.5) даёт ответ 3–5 предложений → показывается в плавающем окне вверху экрана, помеченном «не показывать при записи экрана».

## Сборка и запуск
```bash
./build.sh      # собирает ~/Applications/Notely.app и подписывает
open ~/Applications/Notely.app
```
Подпись: локальный сертификат «Notely Local Signing» в login keychain (не доверенный, только чтобы разрешения macOS не слетали при пересборке).

## Ключи (связка ключей macOS)
`notely-deepgram`, `notely-openrouter`. Обновить: `security add-generic-password -s notely-openrouter -a notely -w '<ключ>' -U -A`

## Горячие клавиши (⌃⌥ = Control+Option)
Space — показать/скрыть · I — поле ввода · S — скрин экрана и сразу решить · P — пауза · +/− шрифт · ←/→ ширина · ↑/↓ листать ответ.
В поле ввода: текст и/или Cmd+V картинки, Enter — отправить, Esc — убрать картинку / очистить.

## Файлы
- `~/Library/Application Support/Notely/profile.md` — резюме/вакансия/стиль (меню → Профиль). Комментарии `<!-- -->` в модель не уходят.
- `~/Library/Application Support/Notely/notely.log` — лог (в т.ч. расшифровка и ответы).
- Тест без окна: `.build/release/Notely --test-file audio.wav` — файл подаётся как голос собеседника.

## Невидимость
Окно `sharingType = .none`. На macOS 15.4+ новые способы захвата могут это игнорировать — надёжно: в созвоне показывать одно окно, а не весь экран. Меню → «Проверить невидимость окна».

## Windows (alpha)

Отдельный клиент Windows 11 x64: [инструкция и ограничения](windows/README.md).
Готовый portable-архив `Notely-Windows-x64` собирается в GitHub Actions.
Для запуска достаточно распаковать архив, открыть `Notely.exe` и ввести ключи
Deepgram/OpenRouter. Реальный звук и скрытие в Zoom/Meet/Telemost требуют проверки
на Windows-ПК. Исходники и сборка macOS от Windows-клиента не зависят.
