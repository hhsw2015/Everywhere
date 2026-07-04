import { describe, expect, it } from "vitest";
import { createAppI18n, resolveInitialLang, supportedLangs } from "./i18n";

describe("resolveInitialLang", () => {
  it("uses a stored supported language first", () => {
    expect(resolveInitialLang({ storedLang: "zh-CN", detectedLang: "en" })).toBe("zh-CN");
  });

  it("uses the detected supported language when no stored language exists", () => {
    expect(resolveInitialLang({ storedLang: null, detectedLang: "zh-CN" })).toBe("zh-CN");
  });

  it("falls back to English for unsupported values", () => {
    expect(resolveInitialLang({ storedLang: "fr", detectedLang: "ja" })).toBe("en");
  });
});

describe("createAppI18n", () => {
  it("creates an i18n instance with app translations", () => {
    const i18n = createAppI18n("zh-CN");

    expect(i18n.lang).toBe("zh-CN");
    expect(i18n.t("nav.providers")).toBe("提供商");
    expect(supportedLangs).toEqual(["en", "zh-CN"]);
  });
});
