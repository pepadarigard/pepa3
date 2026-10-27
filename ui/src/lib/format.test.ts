import { describe, expect, it } from "vitest";
import { formatSize, relative } from "./format";

describe("размер файла", () => {
  it("байты, килобайты и мегабайты по-русски", () => {
    expect(formatSize(512)).toBe("512 Б");
    expect(formatSize(2048)).toBe("2 КБ");
    expect(formatSize(3.4 * 1024 * 1024)).toBe("3,4 МБ");
    expect(formatSize(-1)).toBe("—");
  });
});

describe("относительное время", () => {
  it("несколько секунд — «только что», дальше — с числом", () => {
    expect(relative(new Date(Date.now() - 1000).toISOString())).toBe("только что");
    expect(relative(new Date(Date.now() + 1000).toISOString())).toBe("сейчас");
    expect(relative(new Date(Date.now() - 3 * 60_000).toISOString())).toMatch(/^3 мин.* назад$/);
  });
});
