using System.Collections.Generic;
using System.Text.Json.Serialization;
using Tesserafin.Extensions.Json.Converters;

namespace Tesserafin.Extensions.Tests.Json.Models
{
    /// <summary>
    /// The generic body <c>IReadOnlyList</c> model.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public sealed class GenericBodyIReadOnlyListModel<T>
    {
        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        [JsonConverter(typeof(JsonCommaDelimitedCollectionConverterFactory))]
        public IReadOnlyList<T> Value { get; set; } = default!;
    }
}
