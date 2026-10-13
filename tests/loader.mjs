export async function resolve(specifier, context, next) {
  if (specifier === '@cove/runtime/api') return { url: new URL('./fake-api.mjs', import.meta.url).href, shortCircuit: true };
  if (specifier === '@cove/runtime/react') return next('react', context);
  if (specifier === '@cove/runtime/react-dom-client') return next('react-dom/client', context);
  return next(specifier, context);
}
