import { describe, expect, it } from "vitest";
import type { VariableDefinition } from "../api/types";
import { initialText, isConcealed, isMultiline } from "./fixRetry";

const variable = (name: string, type: VariableDefinition["type"], secret = false): VariableDefinition =>
  ({ name, type, secret, isInput: false, default: null, description: null }) as unknown as VariableDefinition;

describe("fix and retry fields", () => {
  it("does not prefill a value the server showed with a hidden secret", () => {
    const hidden = new Set(["url"]);
    expect(isConcealed(variable("url", "text"), hidden)).toBe(true);
    expect(initialText(variable("url", "text"), "https://x.test/?k=••••", hidden)).toBe("");
    expect(initialText(variable("password", "text", true), "••••", new Set())).toBe("");
    expect(initialText(variable("chat", "text"), "@ivn", hidden)).toBe("@ivn");
    expect(initialText(variable("items", "list"), [1, 2], hidden)).toBe("[\n  1,\n  2\n]");
  });

  it("keeps line breaks of a multi-line text in a multi-line field", () => {
    expect(isMultiline(variable("note", "text"), "первая\nвторая")).toBe(true);
    expect(isMultiline(variable("note", "text"), "одна строка")).toBe(false);
    expect(isMultiline(variable("data", "object"), "{}")).toBe(true);
  });
});
