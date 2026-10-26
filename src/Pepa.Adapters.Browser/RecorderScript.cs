namespace Pepa.Adapters.Browser;

/// <summary>
/// Скрипт записи, который Pepa добавляет на страницы: ловит нажатия, ввод, выбор и клавиши пользователя (только
/// настоящие, не программные события) и сообщает их Pepa с селектором элемента. Селектор — самый устойчивый из
/// доступных: id, data-testid, name, подпись, текст кнопки; путь по положению — последний вариант, с пометкой.
/// </summary>
internal static class RecorderScript
{
    public const string Source = """
        (() => {
          if (window.top !== window || window.__pepaRecorder) return;
          window.__pepaRecorder = true;
          const send = (action) => { try { window.__pepaRecord(JSON.stringify(action)); } catch (e) { } };
          const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
          const quote = (v) => v.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
          const unique = (sel) => { try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; } };
          const dynamicId = (id) => /\d{4,}|[0-9a-f]{8,}|^[0-9]|:/.test(id);
          const textOf = (el) => norm(el.innerText || el.textContent || "");
          const label = (el) => {
            const aria = el.getAttribute("aria-label");
            if (aria) return norm(aria);
            if (el.labels && el.labels[0]) return textOf(el.labels[0]);
            const placeholder = el.getAttribute("placeholder");
            if (placeholder) return norm(placeholder);
            const text = textOf(el) || norm(el.value);
            return text.length <= 40 ? text : text.slice(0, 40) + "…";
          };
          const byText = (el) => {
            const text = textOf(el);
            if (!text || text.length > 40) return null;
            const tag = el.tagName.toLowerCase();
            const same = Array.from(document.querySelectorAll(tag)).filter((x) => textOf(x) === text);
            return same.length === 1 ? tag + ':text-is("' + quote(text) + '")' : null;
          };
          const cssPath = (el) => {
            const parts = [];
            let node = el;
            while (node && node.nodeType === 1 && node !== document.body && node !== document.documentElement) {
              if (node.id && !dynamicId(node.id)) {
                parts.unshift("#" + CSS.escape(node.id));
                const sel = parts.join(" > ");
                if (unique(sel)) return sel;
                break;
              }
              const tag = node.tagName.toLowerCase();
              const parent = node.parentElement;
              const siblings = parent ? Array.from(parent.children).filter((c) => c.tagName === node.tagName) : [node];
              parts.unshift(siblings.length > 1 ? tag + ":nth-of-type(" + (siblings.indexOf(node) + 1) + ")" : tag);
              const sel = parts.join(" > ");
              if (unique(sel)) return sel;
              node = parent;
            }
            return parts.join(" > ");
          };
          const selectorFor = (el) => {
            const tag = el.tagName.toLowerCase();
            if (el.id && !dynamicId(el.id) && unique("#" + CSS.escape(el.id))) return { selector: "#" + CSS.escape(el.id) };
            for (const attr of ["data-testid", "data-test", "data-qa", "name", "aria-label", "placeholder", "title"]) {
              const v = el.getAttribute(attr);
              if (v && v.length <= 80) {
                const sel = tag + "[" + attr + '="' + quote(v) + '"]';
                if (unique(sel)) return { selector: sel };
              }
            }
            const text = byText(el);
            if (text) return { selector: text };
            const href = tag === "a" ? el.getAttribute("href") : null;
            if (href && href.length < 120) {
              const sel = 'a[href="' + quote(href) + '"]';
              if (unique(sel)) return { selector: sel };
            }
            return {
              selector: cssPath(el),
              note: "элемент найден по положению на странице — если сайт изменится, проверьте селектор",
            };
          };
          const isText = (el) =>
            (el.tagName === "INPUT" && !["checkbox", "radio", "button", "submit", "reset", "file", "image", "range", "color"].includes(el.type)) ||
            el.tagName === "TEXTAREA" || el.isContentEditable;
          let pending = null;
          const flush = () => {
            if (!pending) return;
            const p = pending;
            pending = null;
            if (p.el.type === "password") {
              send({ kind: "fill", selector: p.selector, value: "", label: p.label, secret: true, note: "пароль не записан — подставьте переменную" });
            } else {
              send({ kind: "fill", selector: p.selector, value: p.el.isContentEditable ? p.el.innerText : p.el.value, label: p.label, note: p.note });
            }
          };
          window.__pepaFlush = flush;
          document.addEventListener("input", (e) => {
            const el = e.target;
            if (!e.isTrusted || !(el instanceof Element) || !isText(el)) return;
            if (!pending || pending.el !== el) {
              flush();
              const s = selectorFor(el);
              pending = { el, selector: s.selector, note: s.note, label: label(el) };
            }
          }, true);
          document.addEventListener("change", (e) => {
            const el = e.target;
            if (!e.isTrusted || !(el instanceof Element)) return;
            flush();
            if (el.tagName === "SELECT") {
              const option = el.selectedOptions[0];
              const s = selectorFor(el);
              send({ kind: "select", selector: s.selector, value: option ? norm(option.label || option.textContent) : "", label: label(el), note: s.note });
            } else if (el.type === "checkbox" || el.type === "radio") {
              const s = selectorFor(el);
              send({ kind: el.checked ? "check" : "uncheck", selector: s.selector, label: label(el), note: s.note });
            }
          }, true);
          document.addEventListener("click", (e) => {
            if (!e.isTrusted || e.button !== 0 || !(e.target instanceof Element)) return;
            const el = e.target;
            const field = el.closest("input, textarea, select, [contenteditable]");
            // Фокус в поле, выбор в списке, отметка флажка — запишутся своими событиями.
            if (field && (isText(field) || field.tagName === "SELECT" || field.type === "checkbox" || field.type === "radio")) return;
            const labelEl = el.closest("label");
            if (labelEl && labelEl.control && ["checkbox", "radio"].includes(labelEl.control.type)) return;
            const target = el.closest("a, button, [role=button], [role=link], [role=menuitem], [role=tab], [role=option], input[type=submit], input[type=button], summary, [onclick]") || el;
            flush();
            const s = selectorFor(target);
            send({ kind: "click", selector: s.selector, label: label(target), note: s.note });
          }, true);
          document.addEventListener("keydown", (e) => {
            if (!e.isTrusted || !["Enter", "Escape", "Tab"].includes(e.key) || !(e.target instanceof Element) || !isText(e.target)) return;
            const el = e.target;
            const selector = pending && pending.el === el ? pending.selector : selectorFor(el).selector;
            flush();
            // Tab — просто переход к следующему полю: ввод уже записан, клавишу не пишем.
            if (e.key !== "Tab") send({ kind: "press", selector, value: e.key, label: label(el) });
          }, true);
          window.addEventListener("pagehide", flush, true);
        })();
        """;
}
