// Тексты интерфейса. Все строки — здесь, чтобы позже можно было добавить другие языки.
import type { ProcessAction, ProcessStatus, StepState, VariableType, JournalLevel } from "../api/types";

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
    skip: { label: "Пропустить шаг", hint: "Пропустить шаг с ошибкой и идти дальше." },
  } satisfies Record<ProcessAction, { label: string; hint: string }>,
  confirmations: {
    cancel: "Остановить процесс? Он завершится и не продолжится.",
    retryUnknown:
      "Итог шага неизвестен: действие могло уже выполниться. Повтор может выполнить его второй раз. Повторить всё равно?",
    markSucceeded:
      "Отметить шаг выполненным? Делайте это, только если вы сами убедились, что действие выполнено.",
    skip: "Пропустить шаг и продолжить дальше?",
  },
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
