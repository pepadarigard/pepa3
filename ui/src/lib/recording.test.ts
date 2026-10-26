import { describe, expect, it } from "vitest";
import { recordingToBlocks, type RecordedAction } from "./recording";

const action = (kind: RecordedAction["kind"], extra: Partial<RecordedAction> = {}): RecordedAction =>
  ({ kind, selector: null, value: null, url: null, note: null, label: null, secret: false, ...extra });

describe("запись → блоки", () => {
  it("открывает браузер с первым адресом и строит цепочку действий", () => {
    const blocks = recordingToBlocks(
      [
        action("goto", { url: "https://shop.example/login" }),
        action("fill", { selector: "#email", value: "a@b.c", label: "Почта" }),
        action("fill", { selector: "input[name=\"pass\"]", secret: true, value: "" }),
        action("click", { selector: "button:text-is(\"Войти\")", label: "Войти" }),
        action("select", { selector: "#city", value: "Москва", label: "Город" }),
        action("uncheck", { selector: "#news", label: "Рассылка" }),
        action("press", { selector: "#q", value: "Enter" }),
        action("tab"),
        action("goto", { url: "https://shop.example/cart" }),
      ],
      { browser: "pepa", profileId: null },
    );
    expect(blocks.nodes.map((n) => n.type)).toEqual([
      "browser.open", "browser.fill", "browser.fill", "browser.click", "browser.select", "browser.check", "browser.press", "browser.tab", "browser.goto",
    ]);
    expect(blocks.nodes[0].config).toEqual({ browser: "msedge", keepLogin: true, url: "https://shop.example/login" });
    expect(blocks.nodes[2].config).toEqual({ selector: "input[name=\"pass\"]", text: "{{ vars.password }}" });
    expect(blocks.nodes[3].label).toBe("Нажать «Войти»");
    expect(blocks.nodes[5].config).toEqual({ selector: "#news", state: "uncheck" });
    expect(blocks.variables.map((v) => [v.name, v.isInput])).toEqual([["password", true]]);
    expect(blocks.edges).toHaveLength(blocks.nodes.length - 1);
    expect(blocks.edges.every((e, i) => e.source === blocks.nodes[i].id && e.target === blocks.nodes[i + 1].id)).toBe(true);
  });

  it("для AdsPower начинает с профиля", () => {
    const blocks = recordingToBlocks([action("click", { selector: "#go" })], { browser: "adspower", profileId: "k1abc" });
    expect(blocks.nodes[0]).toMatchObject({ type: "adspower.open", config: { profileId: "k1abc" } });
    expect(blocks.nodes[1]).toMatchObject({ type: "browser.click", config: { selector: "#go" } });
  });
});
