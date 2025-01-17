using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace MsSqlCdc;

/// <summary>
/// Is used to identify a distinct LSN value in within the cdc.lsn_time_mapping table
/// with an associated tran_end_time that satisfies the relation when compared to the tracking_time value.
/// </summary>
public enum RelationalOperator
{
    LargestLessThan,
    LargestLessThanOrEqual,
    SmallestGreaterThan,
    SmallestGreaterThanOrEqual
}

/// <summary>
/// An option that governs the content of the metadata columns as well as the rows returned in the result set.
/// </summary>
public enum NetChangesRowFilterOption
{
    /// <summary>
    /// Returns the LSN of the final change to the row and the operation needed
    /// to apply the row in the metadata columns __$start_lsn and __$operation.
    /// The column __$update_mask is always NULL.
    /// </summary>
    All,
    /// <summary>
    /// Returns the LSN of the final change to the row and the operation
    /// needed to apply the row in the metadata columns __$start_lsn and __$operation.
    /// In addition, when an update operation returns (__$operation = 4)
    /// the captured columns modified in the update are marked in the value returned in __$update_mask.
    /// </summary>
    AllWithMask,
    /// <summary>
    /// Returns the LSN of the final change to the row in the metadata columns __$start_lsn.
    /// The column __$operation will be one of two values: 1 for delete and 5 to indicate
    /// that the operation needed to apply the change is either an insert or an update.
    /// The column __$update_mask is always NULL.
    /// </summary>
    AllWithMerge,
}

/// <summary>
/// An option that governs the content of the metadata columns as well as the rows returned in the result set.
/// </summary>
public enum AllChangesRowFilterOption
{
    /// <summary>
    /// Returns all changes within the specified LSN range.
    /// For changes due to an update operation, this option only returns
    /// the row containing the new values after the update is applied.
    /// </summary>
    All,
    /// <summary>
    /// Returns all changes within the specified LSN range.
    /// For changes due to an update operation, this option returns both the row containing
    /// the column values before the update and the row containing the column values after the update.
    /// </summary>
    AllUpdateOld
}

public static class Cdc
{
    /// <summary>
    /// Identifies whether the specified update mask indicates that the specified column
    /// has been updated in the associated change row.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="captureInstance">
    /// Is the name of the capture instance in which the specified column is identified as a captured column.
    /// </param>
    /// <param name="columnName">Is the column to report on.</param>
    /// <param name="updateMask">Is the mask identifying updated columns in any associated change row.</param>
    /// <returns>
    /// Returns whether the specified update mask indicates that the specified column
    /// has been updated in the associated change row.
    /// </returns>
    public static async Task<bool> HasColumnChangedAsync(
        SqlConnection connection,
        string captureInstance,
        string columnName,
        byte[] updateMask)
    {
        var hasColumnChanged = await CdcDatabase.HasColumnChangedAsync(
            connection, captureInstance, columnName, updateMask).ConfigureAwait(false);

        return hasColumnChanged ?? throw new CdcException(
            @$"No returned value from 'HasColumnChanged'
               using values {nameof(captureInstance)}: '{captureInstance}',
               {nameof(columnName)}: '{columnName}',
               {nameof(updateMask)}: '{updateMask}'.");
    }

    /// <summary>
    /// Get the column ordinal of the specified column as it appears in the change
    /// table associated with the specified capture instance.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="captureInstance">
    /// Is the name of the capture instance in which the specified column is identified as a captured column.
    /// </param>
    /// <param name="columnName">Is the column to report on.</param>
    /// <returns>
    /// Returns the column ordinal of the specified column as it appears in the change
    /// table associated with the specified capture instance.
    /// If the column ordinal could not be found -1 is returned.
    /// </returns>
    public static async Task<int> GetColumnOrdinalAsync(
        SqlConnection connection,
        string captureInstance,
        string columnName)
    {
        return await CdcDatabase.GetColumnOrdinalAsync(connection, captureInstance, columnName)
            .ConfigureAwait(false) ?? -1;
    }

    /// <summary>
    /// Map the log sequence number (LSN) value from the start_lsn column
    /// in the cdc.lsn_time_mapping system table for the specified time.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="trackingTime">The datetime value to match against. tracking_time is datetime.</param>
    /// <param name="relationalOperator">
    /// Used to identify a distinct LSN value in within the cdc.lsn_time_mapping table with an associated tran_end_time
    /// that satisfies the relation when compared to the tracking_time value.
    /// </param>
    /// <returns>
    /// Returns the log sequence number (LSN) value from the start_lsn column
    /// in the cdc.lsn_time_mapping system table for the specified time.
    /// </returns>
    public static async Task<BigInteger> MapTimeToLsnAsync(
        SqlConnection connection,
        DateTime trackingTime,
        RelationalOperator relationalOperator)
    {
        var convertedRelationOperator = DataConvert.ConvertRelationOperator(relationalOperator);
        var lsnBytes = await CdcDatabase.MapTimeToLsnAsync(
            connection, trackingTime, convertedRelationOperator).ConfigureAwait(false);

        return lsnBytes is not null
            ? DataConvert.ConvertBinaryLsn(lsnBytes)
            : throw new CdcException(
                @$"Could not map time to lsn using values {nameof(trackingTime)}: '${trackingTime}'
                   and {nameof(relationalOperator)}: '{convertedRelationOperator}. Response was empty.");
    }

    /// <summary>
    /// Map date and time value from the tran_end_time column in the cdc.lsn_time_mapping
    /// system table for the specified log sequence number (LSN).
    /// You can use this function to systematically map LSN ranges to date ranges in a change table.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="lsn">Is the LSN value to match against.</param>
    /// <returns>
    /// Returns the date and time value from the tran_end_time column in the cdc.lsn_time_mapping
    /// system table for the specified log sequence number (LSN).
    /// </returns>
    public static async Task<DateTime> MapLsnToTimeAsync(SqlConnection connection, BigInteger lsn)
    {
        var binaryLsn = DataConvert.ConvertLsnBigEndian(lsn);
        return await CdcDatabase.MapLsnToTime(connection, binaryLsn).ConfigureAwait(false) ??
            throw new CdcException($"Could not convert LSN to time with LSN being '{lsn}'");
    }

    /// <summary>
    /// Get the start_lsn column value for the specified capture instance from the cdc.change_tables system table.
    /// This value represents the low endpoint of the validity interval for the capture instance.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="captureInstance">The name of the capture instance.</param>
    /// <returns>Return the low endpoint of the change data capture timeline for any capture instance.</returns>
    public static async Task<BigInteger> GetMinLsnAsync(SqlConnection connection, string captureInstance)
    {
        var minLsnBytes = await CdcDatabase.GetMinLsnAsync(connection, captureInstance).ConfigureAwait(false);

        return minLsnBytes is not null
            ? DataConvert.ConvertBinaryLsn(minLsnBytes)
            : throw new CdcException(
                @$"Could'nt get min LSN using values {nameof(captureInstance)}: '${captureInstance}'");
    }

    /// <summary>
    /// Get the maximum log sequence number (LSN) from the start_lsn column in the cdc.lsn_time_mapping system table.
    /// You can use this function to return the high endpoint of the change
    /// data capture timeline for any capture instance.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <returns>Return the high endpoint of the change data capture timeline for any capture instance.</returns>
    public static async Task<BigInteger> GetMaxLsnAsync(SqlConnection connection)
    {
        var maxLsnBytes = await CdcDatabase.GetMaxLsnAsync(connection).ConfigureAwait(false);

        return maxLsnBytes is not null
            ? DataConvert.ConvertBinaryLsn(maxLsnBytes)
            : throw new CdcException($"Could not get max LSN.");
    }

    /// <summary>
    /// Get the previous log sequence number (LSN) in the sequence based upon the specified LSN.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="lsn">The LSN number that should be used as the point to get the previous LSN.</param>
    /// <returns>Return the high endpoint of the change data capture timeline for any capture instance.</returns>
    public static async Task<BigInteger> GetPreviousLsnAsync(SqlConnection connection, BigInteger lsn)
    {
        var binaryLsn = DataConvert.ConvertLsnBigEndian(lsn);
        var previousLsnBytes = await CdcDatabase.DecrementLsnAsync(connection, binaryLsn).ConfigureAwait(false);

        return previousLsnBytes is not null
            ? DataConvert.ConvertBinaryLsn(previousLsnBytes)
            : throw new CdcException($"Could not get previous lsn on {nameof(lsn)}: '{lsn}'.");
    }

    /// <summary>
    /// Get the next log sequence number (LSN) in the sequence based upon the specified LSN.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="lsn">The LSN number that should be used as the point to get the next LSN.</param>
    /// <returns>Get the next log sequence number (LSN) in the sequence based upon the specified LSN.</returns>
    public static async Task<BigInteger> GetNextLsnAsync(SqlConnection connection, BigInteger lsn)
    {
        var lsnBinary = DataConvert.ConvertLsnBigEndian(lsn);
        var nextLsnBytes = await CdcDatabase.IncrementLsnAsync(connection, lsnBinary).ConfigureAwait(false);

        return nextLsnBytes is not null
            ? DataConvert.ConvertBinaryLsn(nextLsnBytes)
            : throw new CdcException($"Could not get next lsn on {nameof(lsn)}: '{lsn}'.");
    }

    /// <summary>
    /// Get one net change row for each source row changed within the specified Log Sequence Numbers (LSN) range.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="captureInstance">The name of the capture instance.</param>
    /// <param name="fromLsn">The LSN that represents the low endpoint of the LSN range to include in the result set.</param>
    /// <param name="toLsn">The LSN that represents the high endpoint of the LSN range to include in the result set.</param>
    /// <returns>
    /// Returns one net change row for each source row changed within the specified Log Sequence Numbers (LSN) range.
    /// </returns>
    public static async Task<IReadOnlyCollection<NetChangeRow>> GetNetChangesAsync(
        SqlConnection connection,
        string captureInstance,
        BigInteger fromLsn,
        BigInteger toLsn,
        NetChangesRowFilterOption netChangesRowFilterOption = NetChangesRowFilterOption.All)
    {
        var beginLsnBinary = DataConvert.ConvertLsnBigEndian(fromLsn);
        var endLsnBinary = DataConvert.ConvertLsnBigEndian(toLsn);
        var filterOption = DataConvert.ConvertNetChangesRowFilterOption(netChangesRowFilterOption);
        var cdcColumns = await CdcDatabase.GetNetChangesAsync(
            connection, captureInstance, beginLsnBinary, endLsnBinary, filterOption).ConfigureAwait(false);

        return cdcColumns.Select(x => NetChangeRowFactory.Create(x, captureInstance)).ToList();
    }

    /// <summary>
    /// Get one row for each change applied to the source table within the specified log sequence number (LSN) range.
    /// If a source row had multiple changes during the interval, each change is represented in the returned result set.
    /// </summary>
    /// <param name="connection">An open connection to a MS-SQL database.</param>
    /// <param name="captureInstance">The name of the capture instance.</param>
    /// <param name="fromLsn">The LSN that represents the low endpoint of the LSN range to include in the result set.</param>
    /// <param name="toLsn">The LSN that represents the high endpoint of the LSN range to include in the result set.</param>
    /// <returns>
    /// Returns one row for each change applied to the source table within the specified log sequence number (LSN) range.
    /// If a source row had multiple changes during the interval, each change is represented in the returned result set.
    /// </returns>
    public static async Task<IReadOnlyCollection<AllChangeRow>> GetAllChangesAsync(
        SqlConnection connection,
        string captureInstance,
        BigInteger beginLsn,
        BigInteger endLsn,
        AllChangesRowFilterOption allChangesRowFilterOption = AllChangesRowFilterOption.All)
    {
        var beginLsnBinary = DataConvert.ConvertLsnBigEndian(beginLsn);
        var endLsnBinary = DataConvert.ConvertLsnBigEndian(endLsn);
        var filterOption = DataConvert.ConvertAllChangesRowFilterOption(allChangesRowFilterOption);
        var cdcColumns = await CdcDatabase.GetAllChangesAsync(
            connection, captureInstance, beginLsnBinary, endLsnBinary, filterOption).ConfigureAwait(false);

        return cdcColumns.Select(x => AllChangeRowFactory.Create(x, captureInstance)).ToList();
    }
}
