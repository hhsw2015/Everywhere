import { I18nProvider } from "@embra/i18n/react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
import { createAppI18n, readInitialLang } from "./i18n";
import { App } from "./ui";
import "./style.css";

const i18n = createAppI18n(readInitialLang());

// Everywhere daemon serves this SPA under /connector-ui/. Router basename
// mirrors vite's `base` (see 3rd/open-connector/web/vite.config.ts) so
// react-router's useNavigate('/overview') resolves to /connector-ui/overview
// instead of the daemon-root /overview which returns 404.
createRoot(document.getElementById("root")!).render(
  <I18nProvider i18n={i18n}>
    <BrowserRouter basename="/connector-ui">
      <App />
    </BrowserRouter>
  </I18nProvider>,
);
