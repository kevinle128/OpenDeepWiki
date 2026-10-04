import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";

import admin from "@/i18n/messages/en/admin.json";
import apps from "@/i18n/messages/en/apps.json";
import auth from "@/i18n/messages/en/auth.json";
import authUi from "@/i18n/messages/en/auth-ui.json";
import chat from "@/i18n/messages/en/chat.json";
import common from "@/i18n/messages/en/common.json";
import home from "@/i18n/messages/en/home.json";
import mindmap from "@/i18n/messages/en/mindmap.json";
import profile from "@/i18n/messages/en/profile.json";
import recommend from "@/i18n/messages/en/recommend.json";
import repositories from "@/i18n/messages/en/repositories.json";
import settings from "@/i18n/messages/en/settings.json";
import sidebar from "@/i18n/messages/en/sidebar.json";
import subscribe from "@/i18n/messages/en/subscribe.json";
import theme from "@/i18n/messages/en/theme.json";
import ui from "@/i18n/messages/en/ui.json";

export const enMessages = {
  admin,
  apps,
  auth,
  authUi,
  chat,
  common,
  home,
  mindmap,
  profile,
  repositories,
  recommend,
  settings,
  sidebar,
  subscribe,
  theme,
  ui,
};

function withIntl(ui: ReactElement) {
  return (
    <NextIntlClientProvider locale="en" messages={enMessages}>
      {ui}
    </NextIntlClientProvider>
  );
}

/** Renders with the real English messages so tests read the same text as users. */
export function renderWithIntl(ui: ReactElement) {
  const result = render(withIntl(ui));
  return { ...result, rerender: (next: ReactElement) => result.rerender(withIntl(next)) };
}
