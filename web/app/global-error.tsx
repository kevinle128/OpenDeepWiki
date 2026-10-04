"use client";

const UI_LOCALES = ["zh", "en", "ko", "ja", "es", "fr", "de", "pt-BR"] as const;
const DEFAULT_LOCALE = "en";

const messages: Record<string, { title: string; description: string; back: string }> = {
  en: {
    title: "Page failed to load",
    description: "An unexpected error occurred. Please try again in a moment.",
    back: "Back to Home",
  },
  zh: {
    title: "页面加载失败",
    description: "发生了未预期的错误，请稍后重试。",
    back: "返回首页",
  },
  ko: {
    title: "페이지를 로드하지 못했습니다",
    description: "예기치 않은 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.",
    back: "홈으로",
  },
  ja: {
    title: "ページの読み込みに失敗しました",
    description: "予期しないエラーが発生しました。しばらくしてからもう一度お試しください。",
    back: "ホームに戻る",
  },
  es: {
    title: "Error al cargar la página",
    description: "Ocurrió un error inesperado. Inténtalo de nuevo en un momento.",
    back: "Volver al inicio",
  },
  fr: {
    title: "Échec du chargement de la page",
    description: "Une erreur inattendue s'est produite. Veuillez réessayer dans un instant.",
    back: "Retour à l'accueil",
  },
  de: {
    title: "Seite konnte nicht geladen werden",
    description: "Ein unerwarteter Fehler ist aufgetreten. Bitte versuche es in einem Moment erneut.",
    back: "Zurück zur Startseite",
  },
  "pt-BR": {
    title: "Falha ao carregar a página",
    description: "Ocorreu um erro inesperado. Tente novamente em instantes.",
    back: "Voltar ao início",
  },
};

function resolveLocaleFromCookie(): string {
  if (typeof document === "undefined") return DEFAULT_LOCALE;
  const match = document.cookie.match(/NEXT_LOCALE=([^;]+)/);
  if (!match) return DEFAULT_LOCALE;
  const value = match[1].trim();
  return (UI_LOCALES as readonly string[]).includes(value) ? value : DEFAULT_LOCALE;
}

export default function GlobalError() {
  const locale = resolveLocaleFromCookie();
  const copy = messages[locale] ?? messages[DEFAULT_LOCALE];

  return (
    <main className="flex min-h-screen flex-col items-center justify-center px-6 text-center">
      <h1 className="text-3xl font-semibold">{copy.title}</h1>
      <p className="mt-3 text-sm text-muted-foreground">
        {copy.description}
      </p>
      <button
        type="button"
        onClick={() => window.location.assign(new URL("/", window.location.origin))}
        className="mt-6 inline-flex h-10 items-center justify-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
      >
        {copy.back}
      </button>
    </main>
  );
}
