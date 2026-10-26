import { describe, expect, it } from "vitest";
import { formatSize } from "./format";

describe("размер файла", () => {
  it("байты, килобайты и мегабайты по-русски", () => {
    expect(formatSize(512)).toBe("512 Б");
    expect(formatSize(2048)).toBe("2 КБ");
    expect(formatSize(3.4 * 1024 * 1024)).toBe("3,4 МБ");
    expect(formatSize(-1)).toBe("—");
  });
});
