using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Tesserafin.Controller;
using Tesserafin.Database.Implementations;
using Tesserafin.Database.Implementations.Entities;

namespace Tesserafin.Server.Implementations.Users;

/// <summary>
/// Manages the storage and retrieval of display preferences through Entity Framework.
/// </summary>
public sealed class DisplayPreferencesManager : IDisplayPreferencesManager
{
    private readonly IDbContextFactory<TesserafinDbContext> _dbContextFactory;

    // Serializes find-or-create. ItemDisplayPreferences has no unique index on (UserId, ItemId, Client),
    // and legacy duplicates of the global scope must be kept, so the database cannot reject a second
    // row; DisplayPreferences does have one, which turns the same race into a failed request instead.
    // Creation only happens once per scope, so the lock is taken on a cache miss only.
    private readonly Lock _createLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayPreferencesManager"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    public DisplayPreferencesManager(IDbContextFactory<TesserafinDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public DisplayPreferences GetDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        DisplayPreferences? Find() => dbContext.DisplayPreferences
            .Include(pref => pref.HomeSections)
            .FirstOrDefault(pref =>
                pref.UserId.Equals(userId) && pref.Client == client && pref.ItemId.Equals(itemId));

        var prefs = Find();
        if (prefs is not null)
        {
            return prefs;
        }

        lock (_createLock)
        {
            prefs = Find();
            if (prefs is null)
            {
                prefs = new DisplayPreferences(userId, itemId, client);
                dbContext.DisplayPreferences.Add(prefs);
                dbContext.SaveChanges();
            }

            return prefs;
        }
    }

    /// <inheritdoc />
    public ItemDisplayPreferences GetItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // Legacy databases can hold several rows for one scope. Always read the oldest one, so that
        // reads and the update that follows them keep targeting the same row.
        ItemDisplayPreferences? Find() => dbContext.ItemDisplayPreferences
            .Where(pref => pref.UserId.Equals(userId) && pref.ItemId.Equals(itemId) && pref.Client == client)
            .OrderBy(pref => pref.Id)
            .FirstOrDefault();

        var prefs = Find();
        if (prefs is not null)
        {
            return prefs;
        }

        lock (_createLock)
        {
            prefs = Find();
            if (prefs is null)
            {
                prefs = new ItemDisplayPreferences(userId, itemId, client);
                dbContext.ItemDisplayPreferences.Add(prefs);
                dbContext.SaveChanges();
            }

            return prefs;
        }
    }

    /// <inheritdoc />
    public IList<ItemDisplayPreferences> ListItemDisplayPreferences(Guid userId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.ItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId) && !prefs.ItemId.Equals(default) && prefs.Client == client)
            .ToList();
    }

    /// <inheritdoc />
    public Dictionary<string, string?> ListCustomItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.CustomItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId)
                            && prefs.ItemId.Equals(itemId)
                            && prefs.Client == client)
            .ToDictionary(prefs => prefs.Key, prefs => prefs.Value);
    }

    /// <inheritdoc />
    public void SetCustomItemDisplayPreferences(Guid userId, Guid itemId, string client, Dictionary<string, string?> customPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // Replace the whole set atomically: without the transaction the delete commits on its own,
        // so concurrent saves interleave into a mix of both sets (or fail on the unique index), and a
        // failed insert loses every stored preference.
        using var transaction = dbContext.Database.BeginTransaction();
        dbContext.CustomItemDisplayPreferences.Where(prefs => prefs.UserId.Equals(userId)
                            && prefs.ItemId.Equals(itemId)
                            && prefs.Client == client)
                            .ExecuteDelete();

        foreach (var (key, value) in customPreferences)
        {
            dbContext.CustomItemDisplayPreferences
                .Add(new CustomItemDisplayPreferences(userId, itemId, client, key, value));
        }

        dbContext.SaveChanges();
        transaction.Commit();
    }

    /// <inheritdoc/>
    public void UpdateDisplayPreferences(DisplayPreferences displayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // The entity was loaded by another context. Attaching it only marks new home sections as added:
        // sections removed from the collection would stay in the database, and edits to loaded ones would
        // be ignored. Delete the removed ones explicitly and save the loaded ones as modified, in one
        // transaction so a concurrent save cannot interleave with it.
        using var transaction = dbContext.Database.BeginTransaction();
        var keptSectionIds = displayPreferences.HomeSections
            .Where(section => section.Id != 0)
            .Select(section => section.Id)
            .ToList();
        dbContext.Set<HomeSection>()
            .Where(section => section.DisplayPreferencesId == displayPreferences.Id && !keptSectionIds.Contains(section.Id))
            .ExecuteDelete();

        dbContext.DisplayPreferences.Attach(displayPreferences).State = EntityState.Modified;
        foreach (var section in displayPreferences.HomeSections.Where(section => section.Id != 0))
        {
            dbContext.Entry(section).State = EntityState.Modified;
        }

        dbContext.SaveChanges();
        transaction.Commit();
    }

    /// <inheritdoc/>
    public void UpdateItemDisplayPreferences(ItemDisplayPreferences itemDisplayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        dbContext.ItemDisplayPreferences.Attach(itemDisplayPreferences).State = EntityState.Modified;
        dbContext.SaveChanges();
    }
}
