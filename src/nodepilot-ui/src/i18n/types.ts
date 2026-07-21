import 'i18next';

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common';
    /**
     * Strict-resource type augmentation is intentionally disabled. With ~20 namespaces and
     * many dynamic key constructions (`activities:labels.${type}`, `nav:${key}`), enforcing
     * a literal-union for `t()` arguments forces every call-site through a runtime cast.
     * We rely on missing-key warnings at runtime + tests instead.
     */
    returnNull: false;
  }
}
