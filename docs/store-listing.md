# Карточка приложения для магазинов

Готовые тексты и ответы для Google Play и RuStore. Версия: **1.2.0 (versionCode 8)**,
пакет `com.vienlink.app`. Магазинная сборка собирается без самообновления:
`powershell -File build\publish-store.ps1`.

## Название и описания

### Русский

**Название (Play ≤ 30, RuStore ≤ 50):**
`VieNLink — прокси и VPN`

**Краткое описание (до 80):**
`Клиент прокси: проверка узлов, подписки и VPN-подключение в один клик.`

**Полное описание:**
```
VieNLink — клиент для работы с прокси-серверами и подписками (приложение в статусе бета:
в интерфейсе есть отметка «БЕТА»).

Что умеет:
• собирает узлы из открытых списков и ваших подписок (VLESS, VMess, Trojan, Shadowsocks, Hysteria2);
• проверяет доступность узлов реальными запросами и показывает понятную сводку;
• подключается одним нажатием кнопки сервиса — приложение само выбирает живой узел;
• работает как VPN через VpnService и sing-box: весь трафик или только выбранные приложения;
• умеет направлять через узел только нужные домены и приложения;
• ведёт локальный журнал подключений и проверок;
• собирает и проверяет MTProto-прокси для Telegram;
• тёмная и светлая темы, виджет и плитка быстрых действий.

Конфиденциальность: нет аккаунта, рекламы, трекеров и аналитики. Настройки, источники
и журналы хранятся только на телефоне. Ссылки на источники шифруются.

Важно: открытые бесплатные узлы никем не проверяются, а их владельцы видят ваш трафик.
Не используйте такие узлы для банковских и почтовых сервисов. Приложение не расшифровывает HTTPS.

Проект открытый (GPL-3.0), исходный код: https://github.com/VieNchikkk/VieNLink
```

### English

**Title (≤ 30):**
`VieNLink — Proxy & VPN`

**Short description (≤ 80):**
`Proxy client: node checks, subscriptions and one-tap VPN.`

**Full description:**
```
VieNLink is a client for proxy servers and subscriptions (the app is in beta:
the UI shows a «БЕТА» badge).

Features:
• collects nodes from public lists and your own subscriptions (VLESS, VMess, Trojan, Shadowsocks, Hysteria2);
• checks node availability with real requests and shows a clear summary;
• connects with one tap — the app picks a working node for the selected service;
• works as a VPN via VpnService and sing-box: all traffic or selected apps only;
• can route only the domains/apps you choose through the node;
• keeps a local log of connections and checks;
• discovers and checks MTProto proxies for Telegram;
• dark/light themes, home-screen widget and quick-settings tile.

Privacy: no account, no ads, no trackers, no analytics. Settings, sources and logs stay
on the device; subscription links are encrypted at rest.

Note: free public nodes are not audited and their operators can see your traffic.
Do not use them for banking or email. The app does not decrypt HTTPS.

Open source (GPL-3.0): https://github.com/VieNchikkk/VieNLink
```

## Категории и теги

- **Google Play:** категория *Tools*; теги: VPN, proxy, network tools.
- **RuStore:** категория *Инструменты*; возрастной рейтинг **18+** (сетевой клиент,
  предупреждение о недоверенных узлах). Теги: VPN, прокси, сеть, Telegram.

## Контакты и ссылки

- **E-mail разработчика:** указать действующий адрес в кабинете (обязательное поле).
- **Политика конфиденциальности:** после включения GitHub Pages —
  `https://vienchikkk.github.io/VieNLink/privacy.html`
  (или GitHub: `https://github.com/VieNchikkk/VieNLink/blob/main/docs/privacy.md`).
- **Сайт:** `https://github.com/VieNchikkk/VieNLink`
- **Исходный код (GPL-3.0):** `https://github.com/VieNchikkk/VieNLink`

## Медиа

| Материал | Требование | Файл |
|---|---|---|
| Иконка | 512×512 PNG, без прозрачности | `store/icon-512.png` |
| Feature graphic (Play) | 1024×500 PNG | `store/feature-1024x500.png` |
| Скриншоты телефона | 2–8, PNG/JPEG, портрет | `store/screenshots/*.png` |
| Скриншоты RuStore | 1080×1920 и выше, 2–10 | `store/screenshots/*.png` |

## Google Play: Data safety

- Собирает ли приложение данные? **Нет** (нет аккаунтов, аналитики, рекламы).
- Передаёт ли данные третьим лицам? **Нет.**
- Шифрование при передаче: **да** (HTTPS к источникам, GitHub, RSS; трафик — через
  выбранный пользователем узел).
- Удаление данных: пользователь может очистить данные приложения или удалить его.
- Реклама: **нет**. Покупки: **нет**.

## Google Play: VPN-декларация

- Приложение использует `VpnService` как **основную функцию** — туннель для трафика
  пользователя.
- VPN не используется для сбора данных, рекламы или монетизации трафика.
- Пользователь сам выбирает узел; приложение не продаёт VPN-подписки.
- При первом подключении Android показывает системное согласие на VPN.

## Google Play: контент-рейтинг (IARC)

Насилие, секс, азартные игры, наркотики, язык — **нет**. Обмен контентом между
пользователями — нет. Покупки — нет. Реклама — нет. Геолокация — нет.
Ожидаемый рейтинг: **3+ / Everyone**, при желании — 18+ из-за предупреждения о
недоверенных узлах.

## RuStore: что заполнить в кабинете

1. Название, краткое и полное описание (выше), категория «Инструменты», теги.
2. Иконка 512×512 и скриншоты 1080×1920+ (2–10).
3. Политика конфиденциальности (публичный URL).
4. Возрастной рейтинг 18+ и контактный e-mail.
5. APK `VieNLink-1.2.0-store.apk` (в магазинной сборке нет самообновления).
6. Подтвердить, что приложение не содержит рекламы и покупок.

> Юридическое примечание: в РФ реклама средств обхода блокировок ограничена
> (408-ФЗ). Для RuStore используйте нейтральные формулировки из этого файла
> («клиент прокси и подписок», «VPN-подключение»), без обещаний «доступа к
> заблокированным сайтам». Это снижает риск отказа модерации и претензий.
