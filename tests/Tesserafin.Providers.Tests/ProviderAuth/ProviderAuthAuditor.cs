using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>
    /// A structure-aware provider-credential audit of a compiled assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This audit deliberately uses <b>no entropy threshold and no length threshold</b>. Two of the
    /// three credentials Tesserafin inherited were six and eight characters long — indistinguishable
    /// by inspection from any other short string. What makes a value a credential is not how it
    /// looks but <em>where it is used</em>: a literal that reaches an authentication position in a
    /// request.
    /// </para>
    /// <para>
    /// It reads the compiled assembly rather than the source, which is what makes it complete. The
    /// C# compiler folds constant concatenation, constant interpolation and constant fragments into
    /// a single literal before emitting it into the <c>#US</c> user-string heap, so all three
    /// evasions collapse into one observable: a string that runs past an authentication boundary. A
    /// source-level pattern scan would miss every one of them.
    /// </para>
    /// <para>The rules, each independent of the others:</para>
    /// <list type="number">
    /// <item><description>
    /// <b>Auth-boundary termination.</b> For every provider declaring an <c>authBoundary</c>, any
    /// user string beginning with that boundary must end exactly there. One character past the
    /// boundary is a compiled-in credential.
    /// </description></item>
    /// <item><description>
    /// <b>Host-string allowlist.</b> Any user string mentioning a declared provider's host must be
    /// one of that provider's declared <c>allowedHostStrings</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Unregistered authentication path.</b> Any user string containing an authentication marker
    /// (<c>apikey=</c>, <c>access_token=</c>, <c>Authorization:</c>, …) must be exactly some declared
    /// <c>authBoundary</c>. A credential-bearing URL for a host nobody declared fails.
    /// </description></item>
    /// <item><description>
    /// <b>Constant allowlist.</b> Every <c>const string</c> declared by a type in a policed provider
    /// namespace must be named in the inventory. This is what catches a bare key constant that has
    /// not yet been concatenated into a URL, with no heuristics involved.
    /// </description></item>
    /// <item><description>
    /// <b>Credential-reader allowlist.</b> Only the methods the inventory names may read a declared
    /// credential configuration property. This is the provenance rule: an undeclared path from
    /// configuration to a request — including one into a log call or an exception message — fails.
    /// </description></item>
    /// <item><description>
    /// <b>Inventory freshness.</b> A declared auth boundary that no longer appears in the assembly,
    /// or a declared reader that no longer reads, fails. A stale entry is as much a defect as a
    /// missing one.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class ProviderAuthAuditor
    {
        private readonly ProviderAuthInventory _inventory;

        /// <summary>Initializes a new instance of the <see cref="ProviderAuthAuditor"/> class.</summary>
        /// <param name="inventory">The inventory to audit against.</param>
        public ProviderAuthAuditor(ProviderAuthInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);
            _inventory = inventory;
        }

        /// <summary>Audits one compiled assembly.</summary>
        /// <param name="assemblyPath">Path to the assembly to audit.</param>
        /// <param name="policeInventory">
        /// Whether to apply the constant allowlist, the credential-reader allowlist and the
        /// freshness rule. Control fixtures declare none of those, so they are audited on the string
        /// rules alone.
        /// </param>
        /// <returns>Every violation found, in a stable order.</returns>
        public IReadOnlyList<ProviderAuthViolation> Audit(string assemblyPath, bool policeInventory = true)
        {
            var violations = new List<ProviderAuthViolation>();

            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var metadata = peReader.GetMetadataReader();

            var strings = ReadAllStringLiterals(metadata);
            AuditStrings(strings, violations);

            if (policeInventory)
            {
                AuditConstants(metadata, violations);
                AuditCredentialReaders(peReader, metadata, violations);
                AuditFreshness(strings, violations);
            }

            return violations
                .OrderBy(v => v.Rule, StringComparer.Ordinal)
                .ThenBy(v => v.Provider, StringComparer.Ordinal)
                .ThenBy(v => v.Detail, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Reads every string literal an assembly carries — both those loaded by IL, from the
        /// <c>#US</c> user-string heap, and those declared as <c>const</c>, from the Constant table.
        /// </summary>
        /// <remarks>
        /// Both are needed. A constant that IL loads reaches <c>#US</c>; a constant that is only
        /// declared — never used, or used exclusively from another assembly that inlines it — never
        /// does, and would be invisible to a <c>#US</c>-only scan. A credential is a finding whether
        /// or not this assembly happens to use it.
        /// </remarks>
        /// <param name="metadata">The assembly's metadata reader.</param>
        /// <returns>Every string literal in the assembly.</returns>
        public static IReadOnlyList<string> ReadAllStringLiterals(MetadataReader metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            var strings = new List<string>();

            var handle = MetadataTokens.UserStringHandle(0);
            while (true)
            {
                handle = metadata.GetNextHandle(handle);
                if (handle.IsNil)
                {
                    break;
                }

                strings.Add(metadata.GetUserString(handle));
            }

            foreach (var fieldHandle in metadata.FieldDefinitions)
            {
                var defaultValue = metadata.GetFieldDefinition(fieldHandle).GetDefaultValue();
                if (defaultValue.IsNil)
                {
                    continue;
                }

                var constant = metadata.GetConstant(defaultValue);
                if (constant.TypeCode != ConstantTypeCode.String)
                {
                    continue;
                }

                var reader = metadata.GetBlobReader(constant.Value);
                strings.Add(reader.ReadUTF16(reader.RemainingBytes));
            }

            return strings;
        }

        private void AuditStrings(IReadOnlyList<string> strings, List<ProviderAuthViolation> violations)
        {
            var boundaries = _inventory.Providers
                .Where(p => !string.IsNullOrEmpty(p.AuthBoundary))
                .ToArray();

            foreach (var value in strings)
            {
                foreach (var provider in boundaries)
                {
                    if (value.StartsWith(provider.AuthBoundary!, StringComparison.Ordinal)
                        && value.Length > provider.AuthBoundary!.Length)
                    {
                        violations.Add(new ProviderAuthViolation(
                            "auth-boundary-not-terminal",
                            provider.Name,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"a string constant continues {value.Length - provider.AuthBoundary.Length} character(s) past the declared {provider.Name} authentication boundary; the credential must be appended at run time from {provider.ConfigurationType}.{provider.ConfigurationProperty}")));
                    }
                }

                foreach (var provider in _inventory.Providers)
                {
                    if (provider.AllowedHostStrings.Count == 0
                        || !value.Contains(provider.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!provider.AllowedHostStrings.Contains(value, StringComparer.Ordinal))
                    {
                        violations.Add(new ProviderAuthViolation(
                            "undeclared-host-string",
                            provider.Name,
                            $"a string constant mentions the declared {provider.Name} host '{provider.Host}' but is not one of that provider's allowedHostStrings"));
                    }
                }

                var marker = _inventory.AuthMarkers
                    .FirstOrDefault(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
                if (marker is not null
                    && !boundaries.Any(p => string.Equals(p.AuthBoundary, value, StringComparison.Ordinal)))
                {
                    violations.Add(new ProviderAuthViolation(
                        "unregistered-auth-path",
                        "(none)",
                        $"a string constant carries the authentication marker '{marker}' but matches no declared provider authBoundary"));
                }
            }
        }

        private void AuditConstants(MetadataReader metadata, List<ProviderAuthViolation> violations)
        {
            var allowed = _inventory.ConstantAllowlist.Allowed.ToImmutableHashSet(StringComparer.Ordinal);
            var namespaces = _inventory.ConstantAllowlist.Namespaces;

            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                var typeNamespace = metadata.GetString(type.Namespace);
                if (!namespaces.Contains(typeNamespace, StringComparer.Ordinal))
                {
                    continue;
                }

                var typeName = metadata.GetString(type.Name);
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = metadata.GetFieldDefinition(fieldHandle);
                    var defaultValue = field.GetDefaultValue();
                    if (defaultValue.IsNil)
                    {
                        continue;
                    }

                    if (metadata.GetConstant(defaultValue).TypeCode != ConstantTypeCode.String)
                    {
                        continue;
                    }

                    var qualified = typeNamespace + "." + typeName + "." + metadata.GetString(field.Name);
                    if (!allowed.Contains(qualified))
                    {
                        violations.Add(new ProviderAuthViolation(
                            "undeclared-string-constant",
                            typeNamespace,
                            $"'{qualified}' is a string constant in a policed provider namespace and is not in the inventory's constantAllowlist"));
                    }
                }
            }
        }

        private void AuditCredentialReaders(PEReader peReader, MetadataReader metadata, List<ProviderAuthViolation> violations)
        {
            foreach (var provider in _inventory.Configured())
            {
                if (provider.ConfigurationProperty is null || provider.ConfigurationType is null)
                {
                    continue;
                }

                var declared = provider.CredentialReaders.ToImmutableHashSet(StringComparer.Ordinal);
                var actual = FindCallers(peReader, metadata, provider.ConfigurationType, "get_" + provider.ConfigurationProperty);

                foreach (var caller in actual.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    violations.Add(new ProviderAuthViolation(
                        "undeclared-credential-reader",
                        provider.Name,
                        $"'{caller}' reads {provider.ConfigurationType}.{provider.ConfigurationProperty} but is not declared in that provider's credentialReaders; every path from an operator credential to a request, a log or an exception must be declared"));
                }

                foreach (var stale in declared.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    violations.Add(new ProviderAuthViolation(
                        "stale-credential-reader",
                        provider.Name,
                        $"'{stale}' is declared as a {provider.Name} credential reader but no longer reads {provider.ConfigurationProperty}"));
                }
            }
        }

        private void AuditFreshness(IReadOnlyList<string> strings, List<ProviderAuthViolation> violations)
        {
            foreach (var provider in _inventory.Providers)
            {
                if (provider.AuthBoundary is not null
                    && !strings.Contains(provider.AuthBoundary, StringComparer.Ordinal))
                {
                    violations.Add(new ProviderAuthViolation(
                        "stale-inventory-entry",
                        provider.Name,
                        $"the declared authentication boundary for {provider.Name} no longer appears in the assembly; the code path it describes is gone"));
                }
            }
        }

        /// <summary>
        /// Finds every method in the assembly whose IL body contains a <c>call</c> or <c>callvirt</c>
        /// to the named property getter on the named declaring type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a conservative token scan, not a decoder: it looks for the five-byte sequence
        /// <c>0x28|0x6F</c> followed by a metadata token that resolves to the target getter. Because
        /// it does not track instruction boundaries, it can in principle match those five bytes
        /// inside another instruction's operand and report a method that does not actually read the
        /// property. It can never do the reverse — every real call site contains the sequence.
        /// </para>
        /// <para>
        /// That asymmetry is the right one for a gate. A false positive fails closed and is resolved
        /// by a human adding one line to the inventory, which is exactly the review this audit
        /// exists to force; a false negative would let an undeclared credential path ship. A full IL
        /// decoder would remove the false positives at the cost of a hand-maintained opcode table
        /// whose own bugs would produce silent false negatives.
        /// </para>
        /// </remarks>
        private static ImmutableHashSet<string> FindCallers(
            PEReader peReader,
            MetadataReader metadata,
            string declaringType,
            string calleeName)
        {
            var targets = ResolveGetterTokens(metadata, declaringType, calleeName);
            var callers = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            if (targets.IsEmpty)
            {
                return callers.ToImmutable();
            }

            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                var typeNamespace = metadata.GetString(type.Namespace);
                var typeName = metadata.GetString(type.Name);

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                    if (il is null || !ContainsCallTo(il, targets))
                    {
                        continue;
                    }

                    var methodName = metadata.GetString(method.Name);
                    callers.Add(string.IsNullOrEmpty(typeNamespace)
                        ? typeName + "." + methodName
                        : typeNamespace + "." + typeName + "." + methodName);
                }
            }

            return callers.ToImmutable();
        }

        /// <summary>
        /// Resolves the metadata tokens that denote the given property getter, both as a definition
        /// in this assembly and as any reference to it.
        /// </summary>
        private static ImmutableHashSet<int> ResolveGetterTokens(
            MetadataReader metadata,
            string declaringType,
            string calleeName)
        {
            var tokens = ImmutableHashSet.CreateBuilder<int>();

            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                var fullName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
                if (!string.Equals(fullName, declaringType, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var methodHandle in type.GetMethods())
                {
                    if (string.Equals(metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name), calleeName, StringComparison.Ordinal))
                    {
                        tokens.Add(MetadataTokens.GetToken(methodHandle));
                    }
                }
            }

            foreach (var referenceHandle in metadata.MemberReferences)
            {
                var reference = metadata.GetMemberReference(referenceHandle);
                if (!string.Equals(metadata.GetString(reference.Name), calleeName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (reference.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var parent = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
                var fullName = metadata.GetString(parent.Namespace) + "." + metadata.GetString(parent.Name);
                if (string.Equals(fullName, declaringType, StringComparison.Ordinal))
                {
                    tokens.Add(MetadataTokens.GetToken(referenceHandle));
                }
            }

            return tokens.ToImmutable();
        }

        private static bool ContainsCallTo(byte[] il, ImmutableHashSet<int> targets)
        {
            for (var i = 0; i + 5 <= il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F)
                {
                    continue;
                }

                if (targets.Contains(BitConverter.ToInt32(il, i + 1)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
