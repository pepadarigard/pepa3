// Сквозная проверка интерфейса: настоящий pepa-server + Chromium (Playwright).
// Запуск: npm run build && dotnet build ../src/Pepa.Server && npm run e2e
// Скриншоты кладутся в e2e/screenshots.
const { spawn } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");

const root = path.resolve(__dirname, "..", "..");
const serverDll = process.env.PEPA_SERVER_DLL ?? path.join(root, "src/Pepa.Server/bin/Debug/net10.0/pepa-server.dll");
const ui = path.join(root, "src/Pepa.Host/wwwroot");
const shots = path.join(__dirname, "screenshots");
const port = Number(process.env.PEPA_E2E_PORT ?? 5199);
const token = "e2e-token";
const base = `http://127.0.0.1:${port}`;

const assistantPort = port + 1;

// Имитация Claude API для ИИ-помощника: черновик из двух блоков и сведения о модели.
function startFakeAnthropic() {
  const http = require("node:http");
  const draft = {
    summary: "Пишет приветствие в журнал и заканчивается.",
    questions: ["Кого приветствовать?"],
    variables: [{ name: "guest", type: "text", description: "Кого приветствовать", isInput: true, default: "мир" }],
    blocks: [
      { id: "hello", type: "log", label: "Поздороваться", note: "", settings: [{ field: "message", value: "Привет, {{ vars.guest }}!" }] },
      { id: "done", type: "end", label: "", note: "", settings: [{ field: "message", value: "Готово" }] },
    ],
    links: [{ from: "hello", port: "out", to: "done" }],
  };
  // Правка: приветствие меняется на «Здравствуйте»; объяснение: по строчке на блок. Сценарий берётся из запроса.
  const answer = (text) => {
    const scenario = text.includes("<сценарий>")
      ? JSON.parse(text.slice(text.indexOf("<сценарий>") + "<сценарий>".length, text.indexOf("</сценарий>")))
      : null;
    if (scenario && text.startsWith("Объясни")) {
      return {
        summary: "Сценарий здоровается в журнале и заканчивается.",
        steps: scenario.blocks.map((b) => ({ block: b.id, text: `Шаг «${b.type}».` })),
        cautions: ["Проверьте текст приветствия."],
      };
    }
    if (scenario) {
      for (const block of scenario.blocks) {
        for (const setting of block.settings) if (block.type === "log" && setting.field === "message") setting.value = "Здравствуйте, {{ vars.guest }}!";
      }
      return { summary: "Приветствие стало вежливее.", questions: [], variables: [], blocks: scenario.blocks, links: scenario.links };
    }
    return draft;
  };
  const server = http.createServer((req, res) => {
    let body = "";
    req.on("data", (chunk) => (body += chunk));
    req.on("end", () => {
      res.setHeader("content-type", "application/json");
      if (req.url.startsWith("/v1/models/")) {
        res.end(JSON.stringify({ type: "model", id: "claude-opus-5-5", display_name: "Claude Opus 5.5", created_at: "2026-09-01T00:00:00Z" }));
        return;
      }
      const content = JSON.parse(body).messages[0].content;
      const text = typeof content === "string" ? content : content.map((part) => part.text ?? "").join("");
      res.end(JSON.stringify({
        id: "msg_e2e", type: "message", role: "assistant", model: "claude-opus-5-5",
        content: [{ type: "text", text: JSON.stringify(answer(text)) }],
        stop_reason: "end_turn", stop_sequence: null,
        usage: { input_tokens: 1500, output_tokens: 700, cache_read_input_tokens: 0, cache_creation_input_tokens: 0 },
      }));
    });
  });
  server.listen(assistantPort, "127.0.0.1");
  return server;
}

const temp = fs.mkdtempSync(path.join(os.tmpdir(), "pepa-e2e-"));
const workspace = path.join(temp, "workspace");
fs.mkdirSync(shots, { recursive: true });

function step(name) {
  console.log(`▶ ${name}`);
}

async function waitForServer() {
  for (let i = 0; i < 100; i++) {
    try {
      const response = await fetch(`${base}/api/info`, { headers: { "X-Pepa-Token": token } });
      if (response.ok) return;
    } catch {
      // ещё не поднялся
    }
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error("Сервер не запустился");
}

async function main() {
  const anthropic = startFakeAnthropic();
  const server = spawn("dotnet", [serverDll, "--data-dir", path.join(temp, "data"), "--workspace", workspace, "--port", String(port), "--token", token, "--ui", ui,
    "--assistant-url", `http://127.0.0.1:${assistantPort}`], {
    stdio: ["ignore", "pipe", "pipe"],
  });
  let serverLog = "";
  server.stdout.on("data", (d) => (serverLog += d));
  server.stderr.on("data", (d) => (serverLog += d));

  const browser = await chromium.launch();
  const errors = [];
  try {
    await waitForServer();
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: "ru-RU" });
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
    page.on("dialog", (d) => d.accept());

    step("Главная: примеры на месте");
    await page.goto(`${base}/#token=${token}`);
    await page.getByText("Пример 1. Привет, файл").first().waitFor();
    await page.getByText("Движок на связи").waitFor();
    await page.screenshot({ path: path.join(shots, "01-home.png") });

    step("Запуск примера 1 со входным значением");
    await page.goto(`${base}/#/scenarios`);
    const card = page.locator(".scenario-card", { hasText: "Пример 1. Привет, файл" });
    await card.getByRole("button", { name: "Запустить" }).click();
    await page.getByRole("dialog").getByRole("textbox").first().fill("Тест");
    await page.getByRole("dialog").getByRole("button", { name: "Запустить" }).click();
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor({ timeout: 15000 });
    await page.getByText("В файле: Привет, Тест!").waitFor();
    await page.screenshot({ path: path.join(shots, "02-process-completed.png") });
    const hello = fs.readFileSync(path.join(workspace, "примеры", "привет.txt"), "utf8");
    if (!hello.startsWith("Привет, Тест!")) throw new Error(`Неожиданное содержимое файла: ${hello}`);

    step("Новый сценарий в конструкторе");
    await page.goto(`${base}/#/scenarios`);
    await page.getByRole("button", { name: "Новый сценарий" }).click();
    await page.locator(".react-flow__node").first().waitFor();
    await page.getByLabel("Свойства").getByRole("textbox").first().fill("E2E: записать итог");
    await page.locator(".palette-item", { hasText: "Записать файл" }).click();
    const inspector = page.getByLabel("Свойства");
    await inspector.getByText("Записать файл").first().waitFor();
    await inspector.locator(".field", { hasText: "Файл" }).locator("input").fill("e2e/итог-{{ process.id }}.txt");
    await inspector.locator(".field", { hasText: "Текст" }).locator("textarea").fill("Создано из конструктора");

    const newNode = page.locator(".react-flow__node.selected");
    const newId = await newNode.getAttribute("data-id");
    // Начало → Запись (заменит связь Начало → Завершение), Запись → Завершение.
    await drag(page, '.react-flow__node[data-id="start"] .react-flow__handle.source', `.react-flow__node[data-id="${newId}"] .react-flow__handle.target`);
    await drag(page, `.react-flow__node[data-id="${newId}"] .react-flow__handle[data-handleid="out"]`, '.react-flow__node[data-id="end"] .react-flow__handle.target');
    await page.locator('.canvas-issues[data-state="valid"]').waitFor({ timeout: 10000 });
    await page.screenshot({ path: path.join(shots, "03-editor-new.png") });

    await page.keyboard.press("Control+s");
    await page.locator(".badge", { hasText: "Сохранено" }).waitFor();

    step("Отмена и возврат");
    await page.locator(".react-flow__pane").click({ position: { x: 20, y: 20 } });
    await page.keyboard.press("Control+z");
    await page.locator(".badge", { hasText: "Не сохранено" }).waitFor();
    await page.keyboard.press("Control+y");
    await page.locator(".badge", { hasText: "Сохранено" }).waitFor();
    await page.locator('.canvas-issues[data-state="valid"]').waitFor({ timeout: 10000 });
    const saved = await (await fetch(`${base}/api/scenarios/${page.url().split("/editor/")[1]}`, { headers: { "X-Pepa-Token": token } })).json();
    const edges = saved.definition.edges.map((e) => `${e.source}:${e.sourcePort}->${e.target}`).sort();
    if (edges.length !== 2 || !edges.includes(`start:out->${newId}`) || !edges.includes(`${newId}:out->end`)) {
      throw new Error(`Связи сохранены неверно: ${edges.join(", ")}`);
    }

    step("Запуск нового сценария из конструктора");
    await page.getByRole("button", { name: "Запустить", exact: true }).click();
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor({ timeout: 15000 });
    const written = fs.readdirSync(path.join(workspace, "e2e"));
    if (written.length !== 1) throw new Error(`Ожидался один файл, найдено: ${written}`);
    await page.screenshot({ path: path.join(shots, "04-process-from-editor.png") });

    step("Копирование блока в другой сценарий и поиск по схеме");
    await page.goto(`${base}/#/editor/${saved.id}`);
    await page.locator(`.react-flow__node[data-id="${newId}"]`).click();
    await page.keyboard.press("Control+c");
    await page.goto(`${base}/#/scenarios`);
    await page.getByRole("button", { name: "Новый сценарий" }).click();
    await page.locator(".react-flow__node").first().waitFor();
    await page.locator(".react-flow__pane").click({ position: { x: 20, y: 20 } });
    await page.keyboard.press("Control+v");
    await page.locator(".react-flow__node").nth(2).waitFor();
    if ((await page.locator(".react-flow__node").count()) !== 3) throw new Error("Блок не вставился");
    await page.locator(".react-flow__pane").click({ position: { x: 20, y: 20 } });
    await page.keyboard.press("Control+f");
    await page.getByPlaceholder("Найти блок: подпись, тип, параметр…").fill("итог");
    await page.keyboard.press("Enter");
    await page.getByLabel("Свойства").locator(".field", { hasText: "Файл" }).locator("input").waitFor();
    const pastedPath = await page.getByLabel("Свойства").locator(".field", { hasText: "Файл" }).locator("input").inputValue();
    if (!pastedPath.startsWith("e2e/итог-")) throw new Error(`Вставлен не тот блок: ${pastedPath}`);
    await page.screenshot({ path: path.join(shots, "14-editor-paste-search.png") });

    step("Новый сценарий из шаблона");
    await page.goto(`${base}/#/scenarios`);
    await page.getByRole("button", { name: "Из шаблона" }).click();
    await page.locator(".template-card", { hasText: "Telegram: спросить и дождаться ответа" }).waitFor();
    await page.waitForTimeout(400);
    await page.screenshot({ path: path.join(shots, "16-templates.png") });
    await page.locator(".template-card", { hasText: "Цикл: несколько файлов" }).click();
    await page.locator(".react-flow__node").nth(3).waitFor();
    if (!(await page.locator(".editor-title").innerText()).startsWith("Цикл")) throw new Error("Шаблон не открылся в конструкторе");

    step("Группа блоков: свернуть, сохранить, развернуть");
    const blocks = page.locator(".react-flow__node-pepa");
    // За угол блока: через середину может проходить линия связи.
    await blocks.nth(1).click({ position: { x: 16, y: 10 } });
    await blocks.nth(2).click({ position: { x: 16, y: 10 }, modifiers: ["Control"] });
    await page.keyboard.press("Control+g");
    await page.getByTestId("group-frame").waitFor();
    const shownBefore = await blocks.count();
    await page.getByRole("button", { name: "Свернуть группу" }).click();
    const groupCard = page.getByTestId("group-card");
    await groupCard.waitFor();
    if (!(await groupCard.innerText()).includes("2 блока")) throw new Error("Свёрнутая группа не показывает свои блоки");
    if ((await blocks.count()) !== shownBefore - 2) throw new Error("Блоки свёрнутой группы видны");
    await page.screenshot({ path: path.join(shots, "16b-group-collapsed.png") });
    await page.keyboard.press("Control+s");
    await page.locator(".editor-top .badge", { hasText: /^Сохранено$/ }).waitFor();
    await page.reload();
    await groupCard.waitFor();
    await groupCard.getByRole("button", { name: "Развернуть" }).click();
    await page.getByTestId("group-frame").waitFor();
    // Группа перетаскивается за заголовок вместе со своими блоками.
    const member = await blocks.nth(1).boundingBox();
    const header = await page.locator(".group-frame-header").boundingBox();
    await page.mouse.move(header.x + 40, header.y + header.height / 2);
    await page.mouse.down();
    await page.mouse.move(header.x + 140, header.y + header.height / 2 + 60, { steps: 8 });
    await page.mouse.up();
    const movedMember = await blocks.nth(1).boundingBox();
    if (Math.abs(movedMember.x - member.x) < 50 || Math.abs(movedMember.y - member.y) < 30) throw new Error("Блоки не поехали вместе с группой");
    await page.getByRole("button", { name: "Разгруппировать" }).click();
    await page.getByTestId("group-frame").waitFor({ state: "detached" });

    step("Окно записи действий");
    await page.goto(`${base}/#/editor/${saved.id}`);
    await page.getByRole("button", { name: "Записать", exact: true }).click();
    await page.getByRole("dialog").getByText("Записать действия в браузере").waitFor();
    await page.getByRole("dialog").getByText("Профиль AdsPower").click();
    await page.getByRole("dialog").getByPlaceholder("k1bitq6l").waitFor();
    await page.waitForTimeout(300);
    await page.screenshot({ path: path.join(shots, "17-recorder.png") });
    // Запись в окнах Windows — только в Pepa.exe: причина видна сразу, кнопка записи недоступна.
    await page.getByRole("dialog").getByRole("button", { name: "В окнах Windows" }).click();
    await page.getByRole("dialog").getByText("Записать действия в окнах Windows").waitFor();
    await page.getByRole("dialog").getByText("только в приложении Pepa для Windows").waitFor();
    if (!(await page.getByRole("dialog").getByRole("button", { name: "Начать запись" }).isDisabled())) {
      throw new Error("Запись в окнах доступна без Windows");
    }
    await page.waitForTimeout(200);
    await page.screenshot({ path: path.join(shots, "17c-recorder-windows.png") });
    await page.getByRole("dialog").getByRole("button", { name: "Закрыть" }).click();

    step("Выбор элемента окна: без Windows — понятная причина");
    await page.locator(".palette-item", { hasText: "Нажать в окне" }).click();
    const pickButton = inspector.getByRole("button", { name: "Выбрать элемент в окне" });
    if (!(await pickButton.isDisabled())) throw new Error("Выбор элемента доступен без окна");
    await inspector.locator(".field", { hasText: "Окно (часть заголовка)" }).locator("input").fill("{{ vars.окно }}");
    await inspector.getByText("Окно задано выражением").waitFor();
    await inspector.locator(".field", { hasText: "Окно (часть заголовка)" }).locator("input").fill("Блокнот");
    const errorsBeforePick = errors.length;
    await pickButton.click();
    await page.getByRole("dialog").getByText("только в приложении Pepa для Windows").waitFor();
    // Ответ 409 здесь ожидаем — браузер всё равно пишет о нём в консоль.
    errors.splice(errorsBeforePick, errors.length - errorsBeforePick, ...errors.slice(errorsBeforePick).filter((e) => !e.includes("status of 409")));
    await page.waitForTimeout(200);
    await page.screenshot({ path: path.join(shots, "17b-element-picker.png") });
    await page.getByRole("dialog").getByRole("button", { name: "Закрыть" }).click();
    // Блок не сохраняем: уход без сохранения подтверждается (диалоги в проверке принимаются).
    await page.goto(`${base}/#/scenarios`);
    await page.getByRole("button", { name: "Новый сценарий" }).waitFor();

    step("Отладка: пошаговый запуск");
    await page.goto(`${base}/#/editor/${saved.id}`);
    await page.getByRole("button", { name: "Пошагово" }).click();
    await page.getByText("Пауза перед блоком (отладка)").waitFor({ timeout: 15000 });
    await page.screenshot({ path: path.join(shots, "15-debug-paused.png") });
    await page.getByRole("button", { name: "Продолжить без остановок" }).click();
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor({ timeout: 15000 });

    step("Пауза, продолжение и остановка ожидающего процесса");
    await page.goto(`${base}/#/scenarios`);
    await page.locator(".scenario-card", { hasText: "Пример 3" }).getByRole("button", { name: "Запустить" }).click();
    await page.locator(".badge", { hasText: "Ожидает" }).first().waitFor({ timeout: 10000 });
    await page.screenshot({ path: path.join(shots, "05-waiting.png") });
    await page.getByRole("button", { name: "Приостановить" }).click();
    await page.locator(".badge", { hasText: "Приостановлен" }).first().waitFor();
    await page.getByRole("button", { name: "Продолжить", exact: true }).click();
    await page.locator(".badge", { hasText: "Ожидает" }).first().waitFor();
    // Точно по имени: «Приостановить» тоже содержит «остановить».
    await page.getByRole("button", { name: "Остановить", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Остановить", exact: true }).click();
    await page.locator(".badge", { hasText: "Остановлен" }).first().waitFor();

    step("Пример 5: занять ресурс и дождаться события");
    await page.goto(`${base}/#/scenarios`);
    await page.locator(".scenario-card", { hasText: "Пример 5" }).getByRole("button", { name: "Запустить" }).click();
    await page.getByRole("dialog").getByRole("textbox").first().fill("Z-1");
    await page.getByRole("dialog").getByRole("button", { name: "Запустить" }).click();
    await page.getByText("Ждёт событие «manual / reply» с ключом «Z-1»").first().waitFor({ timeout: 15000 });
    const replyProcess = page.url().split("/process/")[1];
    await page.getByText("Связи и ожидания").waitFor();
    await page.screenshot({ path: path.join(shots, "08-waiting-event.png") });

    await page.goto(`${base}/#/runs`);
    await page.getByText("Профиль браузера (пример)").waitFor();
    await page.getByRole("button", { name: "Отправить событие" }).click();
    const sendDialog = page.getByRole("dialog");
    await sendDialog.locator(".field", { hasText: "Ключ сопоставления" }).locator("input").fill("Z-1");
    await sendDialog.locator(".field", { hasText: "Данные" }).locator("textarea").fill("согласовано");
    await page.screenshot({ path: path.join(shots, "09-send-event.png") });
    await sendDialog.getByRole("button", { name: "Отправить" }).click();
    await page.getByText(/Событие #\d+ отправлено/).waitFor();
    await page.goto(`${base}/#/process/${replyProcess}`);
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor({ timeout: 15000 });
    const reply = fs.readFileSync(path.join(workspace, "примеры", "ответы", "Z-1.txt"), "utf8");
    if (reply !== "Ответ по заказу Z-1: согласовано") throw new Error(`Неожиданный ответ: ${reply}`);
    await page.goto(`${base}/#/runs`);
    await page.locator(".badge", { hasText: "Обработано" }).first().waitFor();
    await page.getByText("Все ресурсы свободны").waitFor();
    await page.screenshot({ path: path.join(shots, "10-runs-events.png") });

    step("Автозапуск по расписанию");
    await page.goto(`${base}/#/scenarios`);
    const helloCard = page.locator(".scenario-card", { hasText: "Пример 1. Привет, файл" });
    await helloCard.getByTitle("Ещё").click();
    await page.getByRole("button", { name: "Автозапуск…" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "По расписанию" }).click();
    const triggerDialog = page.getByRole("dialog");
    await triggerDialog.locator(".field", { hasText: "Название" }).locator("input").fill("Каждое утро");
    await triggerDialog.getByRole("button", { name: "пн" }).click();
    await triggerDialog.getByRole("button", { name: "ср" }).click();
    await page.screenshot({ path: path.join(shots, "11-trigger-editor.png") });
    await triggerDialog.getByRole("button", { name: "Сохранить" }).click();
    await page.getByRole("dialog").getByText("пн, ср в 09:00").waitFor();
    await page.waitForTimeout(400);
    await page.screenshot({ path: path.join(shots, "12-triggers.png") });
    await page.getByRole("dialog").getByRole("button", { name: "Закрыть" }).click();
    await helloCard.getByText("1 из 1 автозапуск").waitFor();
    await page.goto(`${base}/#/`);
    await page.getByText("Ближайшие запуски по расписанию").waitFor();

    step("История и выполнение");
    await page.goto(`${base}/#/history`);
    await page.locator("table.table tbody tr").nth(2).waitFor();
    await page.screenshot({ path: path.join(shots, "06-history.png") });
    await page.goto(`${base}/#/runs`);
    await page.getByText("Сейчас ничего не выполняется").waitFor();

    step("Интеграции: Telegram — проверка данных входа");
    await page.goto(`${base}/#/integrations`);
    const tg = page.getByTestId("telegram-card");
    await tg.getByText("Не подключён").waitFor();
    await tg.getByRole("textbox", { name: "api_id" }).fill("12345");
    await tg.getByRole("textbox", { name: "api_hash" }).fill("abc");
    await tg.getByRole("textbox", { name: "Телефон аккаунта" }).fill("+7 900 123-45-67");
    const before = errors.length;
    await tg.getByRole("button", { name: "Подключить" }).click();
    await tg.getByText("32 символа (цифры", { exact: false }).waitFor();
    // Ответ 400 на неверные данные — ожидаемый; браузер пишет о нём в консоль.
    errors.splice(before, errors.length - before, ...errors.slice(before).filter((e) => !e.includes("status of 400")));
    await page.screenshot({ path: path.join(shots, "13-integrations-telegram.png") });

    step("Интеграции: Telegram — прокси");
    const proxy = tg.getByTestId("telegram-proxy");
    await proxy.getByRole("button", { name: "Настроить" }).click();
    await proxy.getByRole("textbox", { name: "Ссылка на прокси" }).fill("socks5://ivan:pass@127.0.0.1:1");
    await proxy.getByRole("button", { name: "Проверить" }).click();
    await proxy.getByText("недоступен", { exact: false }).waitFor();
    await proxy.getByRole("button", { name: "Сохранить и подключиться" }).click();
    await proxy.getByTestId("telegram-proxy-summary").filter({ hasText: "SOCKS5 127.0.0.1:1 (логин ivan)" }).waitFor();
    if ((await page.content()).includes("ivan:pass")) throw new Error("Пароль прокси остался на странице");
    await page.screenshot({ path: path.join(shots, "13b-telegram-proxy.png") });
    await proxy.getByRole("button", { name: "Без прокси" }).click();
    await proxy.getByTestId("telegram-proxy-summary").filter({ hasText: "без прокси" }).waitFor();

    step("Параллельные ветви: пример 11");
    await page.goto(`${base}/#/scenarios`);
    const parallelCard = page.locator(".scenario-card", { hasText: "Пример 11. Параллельно" });
    await parallelCard.getByRole("button", { name: "Запустить" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Запустить" }).click();
    await page.waitForURL(/#\/process\//);
    await page.locator(".pnode-here").nth(1).waitFor({ timeout: 15000 });
    if ((await page.locator(".pnode-here").count()) !== 2) throw new Error("Ожидались две отметки «процесс здесь»");
    await page.screenshot({ path: path.join(shots, "14-parallel.png") });
    await fetch(`${base}/api/events`, {
      method: "POST",
      headers: { "X-Pepa-Token": token, "Content-Type": "application/json" },
      body: JSON.stringify({ source: "manual", type: "reply", correlationKey: "A-200", payload: "готово" }),
    });
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor({ timeout: 15000 });

    step("ИИ-помощник: ключ и черновик блоков");
    await page.goto(`${base}/#/settings`);
    await page.getByPlaceholder("sk-ant-…").fill("sk-ant-e2e-0123456789abcdef");
    await page.getByRole("button", { name: "Сохранить ключ" }).click();
    await page.getByText("Подключён · ключ …cdef").waitFor();
    await page.getByTestId("plugins-card").getByText("Расширений нет.").waitFor();
    await page.getByRole("button", { name: "Проверить", exact: true }).click();
    await page.getByText("Ключ подходит").waitFor();
    await page.goto(`${base}/#/scenarios`);
    await page.getByRole("button", { name: "Новый сценарий" }).click();
    await page.locator(".react-flow__node").first().waitFor();
    await page.getByRole("button", { name: "ИИ-помощник" }).click();
    const assistant = page.getByRole("dialog");
    await assistant.locator("textarea").fill("Поздороваться в журнале и закончить.");
    await assistant.getByRole("button", { name: "Составить блоки" }).click();
    await assistant.getByText("Блоки (2)").waitFor();
    await assistant.getByText("Кого приветствовать?").waitFor();
    await page.screenshot({ path: path.join(shots, "13-assistant.png") });
    await assistant.getByRole("button", { name: "Вставить в схему" }).click();
    await page.getByText("Первый подключён к «Началу»").waitFor();
    if ((await page.locator(".react-flow__node").count()) !== 3) throw new Error("Помощник: ожидалось 3 блока на схеме");
    if ((await page.locator(".react-flow__edge").count()) !== 2) throw new Error("Помощник: ожидалось 2 связи на схеме");

    step("ИИ-помощник: объяснить и изменить сценарий");
    await page.getByRole("button", { name: "ИИ-помощник" }).click();
    await assistant.getByRole("button", { name: "Объяснить сценарий" }).click();
    await assistant.getByRole("button", { name: "Объяснить", exact: true }).click();
    await assistant.getByTestId("assistant-explanation").getByText("Проверьте текст приветствия.").waitFor();
    await assistant.getByTestId("assistant-explanation").getByRole("button", { name: "Поздороваться" }).waitFor();
    await assistant.getByRole("button", { name: "Объяснить заново" }).click();
    await assistant.getByRole("button", { name: "Изменить сценарий" }).click();
    await assistant.getByLabel("Что изменить").fill("Поздороваться вежливее.");
    await assistant.getByRole("button", { name: "Предложить изменения" }).click();
    await assistant.getByTestId("assistant-changes").getByText("Изменятся (1)").waitFor();
    await page.screenshot({ path: path.join(shots, "13b-assistant-edit.png") });
    await assistant.getByRole("button", { name: "Применить изменения" }).click();
    await page.getByText("Изменения помощника применены").waitFor();
    await page.locator(".pnode-summary", { hasText: "Здравствуйте" }).waitFor();
    if ((await page.locator(".react-flow__node").count()) !== 3) throw new Error("Правка помощника: ожидалось 3 блока");
    if ((await page.locator(".react-flow__edge").count()) !== 2) throw new Error("Правка помощника: ожидалось 2 связи");
    await page.locator(".react-flow__pane").click({ position: { x: 20, y: 20 } });
    await page.keyboard.press("Control+z");
    await page.locator(".pnode-summary", { hasText: "Привет" }).waitFor();

    step("Резервные копии базы");
    await page.goto(`${base}/#/settings`);
    const backupsCard = page.getByTestId("backups-card");
    // Ежедневная копия делается при запуске.
    await backupsCard.getByText("ежедневная").waitFor({ timeout: 10000 });
    await backupsCard.getByRole("button", { name: "Сделать копию сейчас" }).click();
    await backupsCard.getByText("вручную").waitFor();
    await backupsCard.getByText("Как восстановить из копии").click();
    await backupsCard.getByText("назовите её").waitFor();
    await page.waitForTimeout(200);
    await backupsCard.screenshot({ path: path.join(shots, "25-backups.png") });

    step("Уведомления: включить и пробное сообщение без Telegram");
    const notifications = page.getByTestId("notifications-card");
    await notifications.getByRole("checkbox").check();
    await page.getByRole("button", { name: "Сохранить", exact: true }).click();
    await page.getByText("Сохранено").first().waitFor();
    const errorsBeforeTest = errors.length;
    await notifications.getByRole("button", { name: "Отправить пробное сообщение" }).click();
    await page.getByText("Telegram не подключён").first().waitFor();
    // Ответ 409 здесь ожидаем — браузер всё равно пишет о нём в консоль.
    errors.splice(errorsBeforeTest, errors.length - errorsBeforeTest, ...errors.slice(errorsBeforeTest).filter((e) => !e.includes("status of 409")));
    await page.reload();
    if (!(await page.getByTestId("notifications-card").getByRole("checkbox").isChecked())) throw new Error("Настройка уведомлений не сохранилась");

    step("Тёмная тема");
    await page.goto(`${base}/#/settings`);
    await page.getByRole("button", { name: "Тёмная" }).click();
    await page.goto(`${base}/#/process/${await lastProcessId()}`);
    await page.locator(".badge", { hasText: "Завершён" }).first().waitFor();
    await page.screenshot({ path: path.join(shots, "07-process-dark.png") });

    if (errors.length > 0) throw new Error(`Ошибки в консоли браузера:\n${errors.join("\n")}`);
    console.log("✔ Сквозная проверка пройдена");
  } catch (error) {
    console.error("✖", error.message);
    console.error("Журнал сервера:\n", serverLog.slice(-3000));
    process.exitCode = 1;
  } finally {
    await browser.close();
    anthropic.close();
    server.kill("SIGTERM");
    await new Promise((r) => server.once("exit", r));
    fs.rmSync(temp, { recursive: true, force: true });
  }
}

async function lastProcessId() {
  const response = await fetch(`${base}/api/processes?view=all&limit=1`, { headers: { "X-Pepa-Token": token } });
  const [last] = await response.json();
  return last.id;
}

async function drag(page, fromSelector, toSelector) {
  const from = await page.locator(fromSelector).boundingBox();
  const to = await page.locator(toSelector).boundingBox();
  if (!from || !to) throw new Error(`Не найден элемент для соединения: ${fromSelector} → ${toSelector}`);
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 12 });
  await page.mouse.up();
  await page.waitForTimeout(150);
}

main();
