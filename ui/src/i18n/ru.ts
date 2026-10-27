// Тексты интерфейса. Все строки — здесь, чтобы позже можно было добавить другие языки.
import type { EventState, JournalLevel, ProcessAction, ProcessStatus, StepState, SubscriptionState, VariableType } from "../api/types";

export const ru = {
  app: {
    name: "Pepa",
    tagline: "Автоматизации для Windows",
  },
  nav: {
    home: "Главная",
    scenarios: "Сценарии",
    editor: "Конструктор",
    runs: "Выполнение",
    history: "История",
    integrations: "Интеграции",
    settings: "Настройки",
  },
  connection: {
    online: "Движок на связи",
    connecting: "Подключение…",
    offline: "Нет связи с движком",
  },
  common: {
    save: "Сохранить",
    cancel: "Отмена",
    close: "Закрыть",
    delete: "Удалить",
    run: "Запустить",
    open: "Открыть",
    edit: "Изменить",
    duplicate: "Дублировать",
    export: "Экспорт",
    import: "Импорт",
    create: "Создать",
    loading: "Загрузка…",
    retry: "Повторить",
    yes: "Да",
    no: "Нет",
    confirm: "Подтвердить",
    back: "Назад",
    empty: "Пусто",
    details: "Подробнее",
    all: "Все",
  },
  processStatus: {
    running: "Выполняется",
    waiting: "Ожидает",
    paused: "Приостановлен",
    needsAttention: "Требует решения",
    completed: "Завершён",
    failed: "Завершён с ошибкой",
    cancelled: "Остановлен",
  } satisfies Record<ProcessStatus, string>,
  stepState: {
    planned: "Запланирован",
    running: "Выполняется",
    waiting: "Ожидает",
    succeeded: "Выполнен",
    failed: "Не выполнен",
    unknown: "Итог неизвестен",
    skipped: "Пропущен",
    cancelled: "Отменён",
  } satisfies Record<StepState, string>,
  journalLevel: {
    info: "Сообщение",
    warning: "Предупреждение",
    error: "Ошибка",
  } satisfies Record<JournalLevel, string>,
  actions: {
    pause: { label: "Приостановить", hint: "Остановиться на границе между шагами. Текущий шаг доработает." },
    cancel: { label: "Остановить", hint: "Завершить процесс без продолжения. Отменить это нельзя." },
    continue: { label: "Продолжить", hint: "Возобновить выполнение с сохранённого места." },
    verify: {
      label: "Проверить и продолжить",
      hint: "Pepa сверит результат действия, не повторяя его, и продолжит по итогам проверки.",
    },
    retry: { label: "Выполнить шаг ещё раз", hint: "Шаг выполнится повторно." },
    markSucceeded: {
      label: "Считать выполненным",
      hint: "Вы проверили сами, что действие выполнено. Процесс пойдёт дальше без повтора.",
    },
    markFailed: {
      label: "Считать невыполненным",
      hint: "Вы проверили сами, что действие не выполнено. Процесс пойдёт по ветке «Ошибка», а если её нет — остановится с ошибкой.",
    },
    skip: { label: "Пропустить шаг", hint: "Пропустить шаг с ошибкой и идти дальше." },
    runWithoutPauses: {
      label: "Продолжить без остановок",
      hint: "Отладка: выключить паузы перед блоками для этого процесса и выполнить его до конца.",
    },
  } satisfies Record<ProcessAction, { label: string; hint: string }>,
  confirmations: {
    cancel: "Остановить процесс? Он завершится и не продолжится.",
    continueRestored:
      "База восстановлена из копии: после неё процесс мог уже сделать следующие шаги. Продолжение может выполнить их второй раз. Вы проверили, что они не сделаны?",
    retryUnknown:
      "Итог шага неизвестен: действие могло уже выполниться. Повтор может выполнить его второй раз. Повторить всё равно?",
    markSucceeded:
      "Отметить шаг выполненным? Делайте это, только если вы сами убедились, что действие выполнено.",
    markFailed:
      "Отметить шаг невыполненным? Делайте это, только если вы сами убедились, что действие не выполнено: процесс пойдёт по ветке «Ошибка», а если её нет — остановится с ошибкой (шаг можно будет повторить или пропустить).",
    skip: "Пропустить шаг и продолжить дальше?",
  },
  eventState: {
    received: "Принято",
    matched: "Назначено",
    delivered: "Обработано",
    unmatched: "Никому не подошло",
    conflict: "Нужно решение",
    ignored: "Пропущено",
  } satisfies Record<EventState, string>,
  eventStateHint: {
    received: "Событие принято и сейчас сопоставляется с ожидающими процессами.",
    matched: "Событие назначено процессу. Он применит его, когда продолжит работу (например, после возобновления).",
    delivered: "Процесс получил событие, или по нему запущен сценарий.",
    unmatched: "Ни один процесс не ждал такое событие, ничего не запускалось. Событие сохранено.",
    conflict: "Событие одинаково подходит нескольким процессам. Pepa никому его не отдала — выберите сами.",
    ignored: "Вы решили не обрабатывать это событие.",
  } satisfies Record<EventState, string>,
  subscriptionState: {
    active: "Ждёт",
    fulfilled: "Получено",
    expired: "Тайм-аут",
    cancelled: "Отменено",
  } satisfies Record<SubscriptionState, string>,
  variableTypes: {
    any: "Любой",
    text: "Текст",
    number: "Число",
    boolean: "Да/нет",
    list: "Список",
    object: "Объект",
  } satisfies Record<VariableType, string>,
};

export type Strings = typeof ru;
