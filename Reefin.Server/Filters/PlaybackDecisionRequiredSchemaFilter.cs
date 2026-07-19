using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Reefin.Server.Filters;

/// <summary>
/// Declares <c>required</c> exactly those schema members the server already rejects a request for
/// omitting, for the <c>Reefin.Playback.Decision</c> vocabulary only.
/// </summary>
/// <remarks>
/// <para>
/// Issue #51: the published contract carried no <c>required</c> array on 263 of its 401 schemas, so
/// generated clients marked optional members that MVC rejects with 400. Swashbuckle's
/// <c>SupportNonNullableReferenceTypes()</c> (already enabled) only emits <c>nullable</c>; today
/// <c>required</c> is emitted solely from an explicit <c>[Required]</c> attribute.
/// </para>
/// <para>
/// The rule implemented here is deliberately the narrowest one that is *provable* from metadata: a
/// member bound to a primary-constructor parameter that is a <b>reference</b> type, annotated
/// <b>non-nullable</b>, and has <b>no default value</b>. For exactly those, an absent JSON member
/// makes <c>System.Text.Json</c> pass <see langword="null"/> to the constructor, and MVC's implicit
/// <c>[Required]</c> for non-nullable reference types
/// (<c>MvcOptions.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes</c> is left at its
/// <see langword="false"/> default) then answers 400. Nothing is inferred.
/// </para>
/// <para>
/// Deliberately <b>not</b> marked required, because the server accepts them absent:
/// <list type="bullet">
/// <item>value types (<c>bool</c>, <c>int</c>, <c>enum</c>) — absent yields the type default, never
/// <see langword="null"/>, so the implicit <c>[Required]</c> never fires (e.g.
/// <c>DecodeCapabilities.SupportsHls</c>/<c>SupportsDash</c>);</item>
/// <item>parameters carrying a default value — absent yields that default, not
/// <see langword="null"/>;</item>
/// <item>nullable members (<c>T?</c>) — absent and <c>null</c> are indistinguishable and both
/// accepted (e.g. <c>VideoCodecCapability.MaxLevel</c>).</item>
/// </list>
/// Property initialisers are not visible in metadata, which is precisely why the rule is restricted
/// to primary-constructor parameters rather than to all non-nullable reference members. See
/// <c>docs/pr-openapi-required-audit.md</c>.
/// </para>
/// <para>
/// The namespace is matched by exact equality, not prefix, so sibling vocabularies
/// (<c>Reefin.Playback.Engine</c>, <c>.Dlna</c>, <c>.Execution</c>, <c>.Shadow</c>) are untouched.
/// Widening the scope is a separate change that needs its own measurement.
/// </para>
/// </remarks>
public sealed class PlaybackDecisionRequiredSchemaFilter : ISchemaFilter
{
    /// <summary>
    /// The one namespace this filter applies to, matched by exact equality.
    /// </summary>
    public const string TargetNamespace = "Reefin.Playback.Decision";

    /// <inheritdoc />
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only the schema *definition* of a type, never a member or parameter schema that merely
        // uses it - those carry no `required` of their own.
        if (context.MemberInfo is not null || context.ParameterInfo is not null)
        {
            return;
        }

        if (!string.Equals(context.Type.Namespace, TargetNamespace, StringComparison.Ordinal))
        {
            return;
        }

        if (schema is not OpenApiSchema concreteSchema
            || concreteSchema.Properties is not { Count: > 0 } properties)
        {
            return;
        }

        // A positional record exposes exactly one public instance constructor: the primary one.
        // Anything else (hand-written overloads, no public constructor) is not provable, so skip it
        // rather than guess which constructor deserialization would pick.
        var constructors = context.Type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            return;
        }

        // NullabilityInfoContext is documented as not thread-safe; keep one per invocation.
        var nullability = new NullabilityInfoContext();

        // Sorted so the emitted array is byte-stable across runs, as the canonicaliser requires.
        var required = concreteSchema.Required is null
            ? new SortedSet<string>(StringComparer.Ordinal)
            : new SortedSet<string>(concreteSchema.Required, StringComparer.Ordinal);
        var addedAny = false;

        foreach (var parameter in constructors[0].GetParameters())
        {
            if (parameter.ParameterType.IsValueType
                || parameter.HasDefaultValue
                || nullability.Create(parameter).WriteState != NullabilityState.NotNull)
            {
                continue;
            }

            // Match the schema's own property key rather than assuming the serializer's casing.
            var propertyName = properties.Keys.FirstOrDefault(
                key => string.Equals(key, parameter.Name, StringComparison.OrdinalIgnoreCase));

            if (propertyName is not null)
            {
                addedAny |= required.Add(propertyName);
            }
        }

        // Never turn an absent `required` into an empty array - that would be a contract change
        // with no meaning.
        if (addedAny)
        {
            concreteSchema.Required = required;
        }
    }
}
