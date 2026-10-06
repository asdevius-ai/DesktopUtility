/**
 * i18n.js — minimal English / Ukrainian localization.
 * Static markup uses data-i18n / data-i18n-placeholder / data-i18n-aria attributes; scripts call t().
 */
(function () {
    'use strict';

    const STRINGS = {
        en: {
            'conn.eyebrow': 'Remote display',
            'conn.title.a': 'Your iPad.',
            'conn.title.b': 'Your second screen.',
            'conn.lead': 'Use this tablet as an extra monitor or a trackpad for your Windows PC, over your local Wi-Fi.',
            'conn.available': 'Available PCs',
            'conn.refresh': 'Refresh',
            'conn.searching': 'Looking for your PC…',
            'conn.none': 'No PC found. Enter its IP address or scan the QR code shown on the PC.',
            'conn.scan': 'Scan QR code',
            'conn.enterip': 'Enter IP address',
            'conn.connect': 'Connect',
            'conn.tip': '<strong>Tip:</strong> open the QR code with the iPad Camera app. Then tap Share → Add to Home Screen for a full-screen app.',
            'conn.ip.placeholder': '192.168.1.100',

            'onb.title': 'Add to Home Screen',
            'onb.lead': 'For the full-screen experience, install DesktopUtility like an app.',
            'onb.step1': 'Tap <strong>Share</strong> at the bottom of Safari',
            'onb.step2': 'Scroll down and tap <strong>Add to Home Screen</strong>',
            'onb.step3': 'Tap <strong>Add</strong>',
            'onb.continue': 'Continue in browser',

            'pin.title': 'Enter PIN',
            'pin.lead': 'Type the 6-digit code shown on your PC below the QR code.',
            'pin.pair': 'Pair device',
            'pin.invalid': 'That code is wrong or has expired.',
            'pin.locked': 'Too many attempts. Wait a minute and try again.',
            'pin.expired': 'The QR code has expired. Enter the PIN instead.',

            'qr.hint': 'Point the camera at the QR code on your PC',
            'qr.unavailable': 'In-app scanning needs a secure connection. Scan the QR code with the iPad Camera app instead, or enter the PIN.',
            'qr.denied': 'Camera access was denied.',

            'connecting.title': 'Connecting…',
            'connecting.sub': 'Establishing a secure link',
            'connecting.pairing': 'Pairing device…',
            'connecting.stream': 'Starting video stream…',
            'connecting.reconnect': 'Connection lost, reconnecting…',
            'btn.cancel': 'Cancel',

            'mode.extended': 'Second monitor',
            'mode.remote': 'Touchpad',
            'mode.switching.extended': 'Switching to second monitor…',
            'mode.switching.remote': 'Switching to touchpad…',
            'mode.active.extended': 'Second monitor is active',
            'mode.active.remote': 'Touchpad mode is active',
            'mode.failed': 'Could not switch mode on the PC.',
            'bar.mode': 'Mode',
            'bar.quality': 'Quality',
            'bar.keyboard': 'Keyboard',
            'bar.disconnect': 'Disconnect',
            'quality.low': 'Low',
            'quality.medium': 'Medium',
            'quality.high': 'High',
            'session.reconnecting': 'Reconnecting…',

            'toast.connected': 'Connected',
            'toast.disconnected': 'Disconnected',
            'toast.lost': 'Connection lost. Could not reconnect.',
            'toast.enterip': 'Enter the PC IP address first.',
            'toast.timeout': 'The PC did not respond. Check that it is on the same Wi-Fi.',
            'toast.failed': 'Could not connect.',
            'toast.unauthorized': 'This device is no longer paired. Enter the PIN again.',
            'toast.webrtc': 'Could not start the video stream.',
            'toast.badqr': 'That QR code is not a DesktopUtility link.'
        },

        uk: {
            'conn.eyebrow': 'Віддалений екран',
            'conn.title.a': 'Ваш iPad.',
            'conn.title.b': 'Ваш другий екран.',
            'conn.lead': 'Використовуйте планшет як додатковий монітор або тачпад для ПК з Windows — через домашній Wi-Fi.',
            'conn.available': 'Доступні ПК',
            'conn.refresh': 'Оновити',
            'conn.searching': 'Шукаємо ваш ПК…',
            'conn.none': 'ПК не знайдено. Введіть його IP-адресу або скануйте QR-код на ПК.',
            'conn.scan': 'Сканувати QR-код',
            'conn.enterip': 'Ввести IP-адресу',
            'conn.connect': 'Підключитися',
            'conn.tip': '<strong>Порада:</strong> відкрийте QR-код камерою iPad. Потім «Поділитись» → «На початковий екран», щоб працювати на весь екран.',
            'conn.ip.placeholder': '192.168.1.100',

            'onb.title': 'На початковий екран',
            'onb.lead': 'Для роботи на весь екран встановіть DesktopUtility як застосунок.',
            'onb.step1': 'Натисніть <strong>«Поділитись»</strong> унизу Safari',
            'onb.step2': 'Прокрутіть і оберіть <strong>«На початковий екран»</strong>',
            'onb.step3': 'Натисніть <strong>«Додати»</strong>',
            'onb.continue': 'Продовжити в браузері',

            'pin.title': 'Введіть PIN',
            'pin.lead': 'Введіть 6-значний код, який показано на ПК під QR-кодом.',
            'pin.pair': 'Підключити пристрій',
            'pin.invalid': 'Код неправильний або застарів.',
            'pin.locked': 'Забагато спроб. Зачекайте хвилину.',
            'pin.expired': 'QR-код застарів. Введіть PIN.',

            'qr.hint': 'Наведіть камеру на QR-код на ПК',
            'qr.unavailable': 'Сканування в застосунку потребує захищеного з’єднання. Скануйте QR-код камерою iPad або введіть PIN.',
            'qr.denied': 'Доступ до камери заборонено.',

            'connecting.title': 'Підключення…',
            'connecting.sub': 'Встановлюємо захищений зв’язок',
            'connecting.pairing': 'Підключаємо пристрій…',
            'connecting.stream': 'Запускаємо відеопотік…',
            'connecting.reconnect': 'Зв’язок втрачено, перепідключаємось…',
            'btn.cancel': 'Скасувати',

            'mode.extended': 'Другий монітор',
            'mode.remote': 'Тачпад',
            'mode.switching.extended': 'Перемикаємо на другий монітор…',
            'mode.switching.remote': 'Перемикаємо на тачпад…',
            'mode.active.extended': 'Другий монітор активний',
            'mode.active.remote': 'Режим тачпада активний',
            'mode.failed': 'Не вдалося змінити режим на ПК.',
            'bar.mode': 'Режим',
            'bar.quality': 'Якість',
            'bar.keyboard': 'Клавіатура',
            'bar.disconnect': 'Відключитися',
            'quality.low': 'Низька',
            'quality.medium': 'Середня',
            'quality.high': 'Висока',
            'session.reconnecting': 'Перепідключення…',

            'toast.connected': 'Підключено',
            'toast.disconnected': 'Відключено',
            'toast.lost': 'Зв’язок втрачено. Не вдалося перепідключитись.',
            'toast.enterip': 'Спочатку введіть IP-адресу ПК.',
            'toast.timeout': 'ПК не відповідає. Перевірте, що він у тій самій мережі Wi-Fi.',
            'toast.failed': 'Не вдалося підключитися.',
            'toast.unauthorized': 'Цей пристрій більше не підключено. Введіть PIN ще раз.',
            'toast.webrtc': 'Не вдалося запустити відеопотік.',
            'toast.badqr': 'Цей QR-код не є посиланням DesktopUtility.'
        }
    };

    const lang = (navigator.language || 'en').toLowerCase().startsWith('uk') ? 'uk' : 'en';
    document.documentElement.lang = lang;

    function t(key) {
        return (STRINGS[lang] && STRINGS[lang][key]) || STRINGS.en[key] || key;
    }

    /** Applies translations to all marked elements under root. */
    function apply(root = document) {
        root.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
        root.querySelectorAll('[data-i18n-html]').forEach((el) => { el.innerHTML = t(el.dataset.i18nHtml); });
        root.querySelectorAll('[data-i18n-placeholder]').forEach((el) => { el.placeholder = t(el.dataset.i18nPlaceholder); });
        root.querySelectorAll('[data-i18n-aria]').forEach((el) => { el.setAttribute('aria-label', t(el.dataset.i18nAria)); });
    }

    window.t = t;
    window.I18N = { lang, apply };
})();
