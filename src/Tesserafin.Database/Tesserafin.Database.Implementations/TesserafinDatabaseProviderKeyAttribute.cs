namespace Tesserafin.Database.Implementations;

/// <summary>
/// Defines the key of the database provider.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class TesserafinDatabaseProviderKeyAttribute : System.Attribute
{
    // See the attribute guidelines at
    //  http://go.microsoft.com/fwlink/?LinkId=85236
    private readonly string _databaseProviderKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="TesserafinDatabaseProviderKeyAttribute"/> class.
    /// </summary>
    /// <param name="databaseProviderKey">The key on which to identify the annotated provider.</param>
    public TesserafinDatabaseProviderKeyAttribute(string databaseProviderKey)
    {
        _databaseProviderKey = databaseProviderKey;
    }

    /// <summary>
    /// Gets the key on which to identify the annotated provider.
    /// </summary>
    public string DatabaseProviderKey
    {
        get { return _databaseProviderKey; }
    }
}
