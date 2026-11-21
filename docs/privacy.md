# Политика конфиденциальности VieNLink / Privacy Policy

Дата вступления в силу: 21.11.2026

**Коротко:** VieNLink не требует аккаунта, не содержит рекламы и аналитики и не передаёт
ваши данные разработчику. Всё, что нужно для работы (настройки, источники, кэш узлов,
журналы), хранится на устройстве.

## Русский

### 1. Кто мы
VieNLink — клиент для работы с прокси-серверами и подписками. Приложение распространяется
как открытый проект (GPL-3.0): https://github.com/VieNchikkk/VieNLink.

### 2. Какие данные обрабатываются на устройстве
- **Настройки** (режим подключения, порты, правила маршрутизации, тема).
- **Источники и подписки**: ссылки, которые вы добавили, и полученные из них узлы.
  На Windows список источников шифруется средствами Windows (DPAPI), на Android —
  ключом в Android Keystore.
- **Кэш узлов и результаты проверок**: адреса, задержки, отметки доступности.
- **Журналы приложения**: события и ошибки, нужные для диагностики; хранятся локально
  и ротируются.
- **MTProto-прокси Telegram**: найденные прокси и результаты их проверки.

Эти данные не покидают устройство, кроме случаев, описанных в разделе 3.

### 3. Сетевые соединения
Приложение соединяется с сетью, чтобы выполнять свои функции:
- загружает списки и подписки по адресам, которые **вы** указали;
- проверяет узлы HTTP-запросами **через сам узел** (YouTube, Telegram, Discord,
  Cloudflare и список проверочных доменов);
- читает публичные RSS-зеркала Telegram-каналов, если включён сбор MTProto-прокси;
- проверяет обновления и версию ядра на GitHub (в магазинной сборке обновления
  приходят из магазина);
- передаёт ваш трафик через выбранный вами прокси-узел или VPN-туннель.

Обратите внимание: **владелец выбранного узла** технически видит IP-адрес и объём
трафика, а при незашифрованных соединениях — и их содержимое. Приложение не
расшифровывает HTTPS. Бесплатные узлы из открытых списков никем не проверены —
не используйте их для банковских и почтовых сервисов.

### 4. Чего приложение не делает
- Не создаёт аккаунты и не запрашивает персональные данные.
- Не содержит рекламы, трекеров и аналитики.
- Не отправляет журналы, историю посещений и настройки разработчику.
- Не продаёт и не передаёт данные третьим лицам.

### 5. Разрешения Android
- **VPN (VpnService)** — основной режим: весь или выбранный трафик идёт через узел.
- **Уведомления** — состояние туннеля, ход проверки, готовность обновления.
- **Автозапуск** — восстановление туннеля/обновления после перезагрузки, если включено.
- **Установка пакетов** — **только в GitHub-сборке** для обновления APK с релизов.
  В сборке для Google Play/RuStore этого разрешения нет: обновления ставит магазин.

### 6. Дети
Приложение не предназначено для детей и не собирает данные о них.

### 7. Удаление данных
Все данные хранятся локально. Удалить их можно кнопкой очистки кэша/журналов
в приложении, очисткой данных приложения в настройках Android или удалением приложения.

### 8. Изменения
Актуальная версия политики публикуется в этом файле. При существенных изменениях
обновляется дата вступления в силу.

### 9. Контакты
Вопросы и запросы: GitHub Issues — https://github.com/VieNchikkk/VieNLink/issues
(для магазина укажите контактный e-mail разработчика в карточке приложения).

---

## English

### 1. Who we are
VieNLink is a client for proxy servers and subscriptions. It is an open-source project
(GPL-3.0): https://github.com/VieNchikkk/VieNLink.

### 2. Data processed on your device
Settings, user-provided sources/subscriptions and the nodes parsed from them, node cache
and test results, local diagnostic logs, and discovered Telegram MTProto proxies.
The sources list is encrypted with DPAPI on Windows and with an Android Keystore key on
Android. This data stays on the device.

### 3. Network connections
The app connects to the network only to do its job: download the sources you added, test
nodes with HTTP requests through each node (YouTube, Telegram, Discord, Cloudflare and a
list of check domains), read public Telegram RSS mirrors when MTProto collection is
enabled, check updates/core on GitHub (store builds receive updates from the store), and
carry your traffic through the node or VPN tunnel you selected.

The operator of the node you choose can see your IP address and traffic volume, and for
unencrypted connections also their content. The app does not decrypt HTTPS. Free public
nodes are not audited — do not use them for banking or email.

### 4. What the app does not do
No accounts, no personal data requests, no ads, no trackers, no analytics, no upload of
logs or settings to the developer, no sale or sharing of data with third parties.

### 5. Android permissions
VPN (VpnService) for the tunnel; notifications for tunnel/probe/update status; boot
autostart if enabled; package installation **only in the GitHub build** for APK updates.
Store builds do not request package installation.

### 6. Children
The app is not directed to children and does not collect data about them.

### 7. Deletion
All data is local. Delete it via in-app cache/log clearing, Android app-data clearing,
or by uninstalling the app.

### 8. Changes
The current policy is published in this file; the effective date is updated when needed.

### 9. Contact
GitHub Issues — https://github.com/VieNchikkk/VieNLink/issues
(for the store listing, provide the developer contact e-mail).
