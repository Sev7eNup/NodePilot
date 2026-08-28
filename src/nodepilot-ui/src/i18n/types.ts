import 'i18next';

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'common';
    /**
     * Strict resource typing is left off on purpose. With many namespaces and dynamic keys
     * (`activities:labels.${type}`, `nav:${key}`), a literal union for `t()` arguments would
     * force every call site through a runtime cast. Missing keys are caught by runtime
     * warnings and tests instead.
     */
    returnNull: false;
  }
}
