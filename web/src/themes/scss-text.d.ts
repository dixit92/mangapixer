/**
 * A stylesheet imported as plain text (`import text from './x.scss' with { loader: 'text' }`), used only by the theme specs to read
 * the theme sources. The application never imports a stylesheet from TypeScript.
 */
declare module '*.scss' {
  const text: string;
  export default text;
}
