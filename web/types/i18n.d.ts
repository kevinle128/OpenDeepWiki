import common from '@/i18n/messages/zh/common.json';
import theme from '@/i18n/messages/zh/theme.json';
import sidebar from '@/i18n/messages/zh/sidebar.json';
import auth from '@/i18n/messages/zh/auth.json';
import repositories from '@/i18n/messages/zh/repositories.json';

type Messages = {
  common: typeof common;
  theme: typeof theme;
  sidebar: typeof sidebar;
  auth: typeof auth;
  repositories: typeof repositories;
};

declare global {
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type
  interface IntlMessages extends Messages {}
}
