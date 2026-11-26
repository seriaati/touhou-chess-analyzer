import { dotnet } from './_framework/dotnet.js';

const ready = dotnet.create()
  .then(runtime => runtime.getAssemblyExports('TouhouChessAnalyzer.Web.dll'))
  .then(exports => exports.Api);

// The handler is attached before the runtime finishes loading so early requests aren't dropped.
onmessage = async ({ data: { id, method, request } }) => {
  try {
    const api = await ready;
    postMessage({ id, response: JSON.parse(api[method](JSON.stringify(request))) });
  } catch (e) {
    postMessage({ id, error: String(e) });
  }
};
