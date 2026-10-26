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
  const server = spawn("dotnet", [serverDll, "--data-dir", path.join(temp, "data"), "--workspace", workspace, "--port", String(port), "--token", token, "--ui", ui], {
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
    await page.getByRole("button", { name: "Продолжить" }).click();
    await page.locator(".badge", { hasText: "Ожидает" }).first().waitFor();
    await page.getByRole("button", { name: "Остановить" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Остановить" }).click();
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
