using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Tesserafin.Server.Filters;

/// <summary>
/// Declares <c>explode: true</c> on every parameter emitted with <c>style: deepObject</c>, so the
/// document names a serialization OpenAPI actually defines.
/// </summary>
/// <remarks>
/// <para>
/// Issue #226. Swashbuckle emits <c>style: deepObject</c> for a query parameter whose schema is an
/// object — here, the <c>[FromQuery] Dictionary&lt;string, string&gt;? streamOptions</c> parameter of the
/// audio and video stream endpoints — and leaves <c>explode</c> unset. OpenAPI 3.0.4 defines the
/// <c>explode</c> default as <see langword="false"/> for every style other than <c>form</c>, and then
/// states that "despite <c>false</c> being the default for <c>deepObject</c>, the combination of
/// <c>false</c> with <c>deepObject</c> is undefined". The emitted document therefore declared the one
/// combination the specification leaves undefined, and a generator resolving it to the nearest
/// defined reading emits <c>?streamOptions=k,v</c> — which the ASP.NET Core model binder accepts with
/// HTTP 200 and binds to an <b>empty</b> dictionary. The caller's streaming options vanish silently.
/// </para>
/// <para>
/// The predicate is structural, not nominal: it fires on <see cref="ParameterStyle.DeepObject"/>,
/// which Swashbuckle only assigns to an object-shaped query parameter. It does not look at the
/// parameter's name, its route, or its CLR type, so a future object query parameter is corrected the
/// same way rather than becoming a second instance of this defect.
/// </para>
/// <para>
/// This changes what the contract <b>says</b>, never what the server <b>does</b>: no route, binder,
/// controller signature or accepted request is affected. <c>?streamOptions[k]=v</c> — the encoding
/// <c>deepObject</c>/<c>explode: true</c> names — is exactly what the binder already accepts.
/// </para>
/// </remarks>
public sealed class DeepObjectExplodeParameterFilter : IParameterFilter
{
    /// <inheritdoc />
    public void Apply(IOpenApiParameter parameter, ParameterFilterContext context)
    {
        if (parameter is OpenApiParameter concreteParameter
            && concreteParameter.Style == ParameterStyle.DeepObject
            && concreteParameter.Explode is not true)
        {
            concreteParameter.Explode = true;
        }
    }
}
