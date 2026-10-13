export async function resolve(specifier, context, next) {
  if (specifier === '@cove/runtime/api') return { url: new URL('./fake-api.mjs', import.meta.url).href, shortCircuit: true };
  return next(specifier, context);
}
