using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BlockLogger.Services;

internal sealed class RecoveryReceiptIndex
{
	private readonly string journalPath;

	private readonly string indexPath;

	private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

	public RecoveryReceiptIndex(string journalPath)
	{
		this.journalPath = journalPath;
		indexPath = journalPath + ".index.sqlite";
	}

	internal Task InitializeAsync(CancellationToken cancellation = default(CancellationToken))
	{
		return FindAsync(Array.Empty<string>(), cancellation);
	}

	internal async Task<HashSet<string>> FindAsync(IEnumerable<string> requested, CancellationToken cancellation = default(CancellationToken))
	{
		await gate.WaitAsync(cancellation).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			return await Task.Run(() => Find(requested, cancellation), cancellation).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			gate.Release();
		}
	}

	private HashSet<string> Find(IEnumerable<string> requested, CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		using SqliteConnection sqliteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = indexPath,
			Mode = SqliteOpenMode.ReadWriteCreate
		}.ToString());
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS receipts (key TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS checkpoint (id INTEGER PRIMARY KEY, offset INTEGER NOT NULL, stamp INTEGER NOT NULL, anchor TEXT NOT NULL);";
		sqliteCommand.ExecuteNonQuery();
		long num = 0L;
		long num2 = 0L;
		string text = "";
		using (SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand())
		{
			sqliteCommand2.CommandText = "SELECT offset, stamp, anchor FROM checkpoint WHERE id=1";
			using SqliteDataReader sqliteDataReader = sqliteCommand2.ExecuteReader();
			if (sqliteDataReader.Read())
			{
				num = sqliteDataReader.GetInt64(0);
				num2 = sqliteDataReader.GetInt64(1);
				text = sqliteDataReader.GetString(2);
			}
		}
		long num3 = (File.Exists(journalPath) ? new FileInfo(journalPath).Length : 0);
		long num4 = (File.Exists(journalPath) ? File.GetLastWriteTimeUtc(journalPath).Ticks : 0);
		using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
		if (num > 0 && (num3 < num || (num3 == num && num4 != num2) || ContentAnchor(num) != text))
		{
			throw new InvalidDataException("Recovery receipt journal changed or was truncated; preserve journal/index and restore consistent history before recovery.");
		}
		if (num3 > num)
		{
			using FileStream fileStream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			fileStream.Position = num;
			using StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			using SqliteCommand sqliteCommand3 = sqliteConnection.CreateCommand();
			sqliteCommand3.Transaction = sqliteTransaction;
			sqliteCommand3.CommandText = "INSERT OR IGNORE INTO receipts(key) VALUES($key)";
			SqliteParameter sqliteParameter = sqliteCommand3.Parameters.Add("$key", SqliteType.Text);
			int num5 = 0;
			string text2;
			while ((text2 = streamReader.ReadLine()) != null)
			{
				if (++num5 % 256 == 0)
				{
					cancellation.ThrowIfCancellationRequested();
				}
				if (string.IsNullOrWhiteSpace(text2))
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(text2);
				string value = ((jsonDocument.RootElement.TryGetProperty("key", out var value2) && value2.ValueKind == JsonValueKind.String) ? value2.GetString() : null);
				if (string.IsNullOrWhiteSpace(value))
				{
					throw new InvalidDataException("Recovery receipt without key; preserve the journal before repair.");
				}
				sqliteParameter.Value = value;
				sqliteCommand3.ExecuteNonQuery();
			}
		}
		using (SqliteCommand sqliteCommand4 = sqliteConnection.CreateCommand())
		{
			sqliteCommand4.Transaction = sqliteTransaction;
			sqliteCommand4.CommandText = "INSERT OR REPLACE INTO checkpoint(id,offset,stamp,anchor) VALUES(1,$offset,$stamp,$anchor)";
			sqliteCommand4.Parameters.AddWithValue("$offset", num3);
			sqliteCommand4.Parameters.AddWithValue("$stamp", num4);
			sqliteCommand4.Parameters.AddWithValue("$anchor", (num3 > 0) ? ContentAnchor(num3) : "");
			sqliteCommand4.ExecuteNonQuery();
		}
		cancellation.ThrowIfCancellationRequested();
		sqliteTransaction.Commit();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		using SqliteCommand sqliteCommand5 = sqliteConnection.CreateCommand();
		sqliteCommand5.CommandText = "SELECT 1 FROM receipts WHERE key=$key";
		SqliteParameter sqliteParameter2 = sqliteCommand5.Parameters.Add("$key", SqliteType.Text);
		int num6 = 0;
		foreach (string item in requested.Distinct(StringComparer.Ordinal))
		{
			if (++num6 % 256 == 0)
			{
				cancellation.ThrowIfCancellationRequested();
			}
			sqliteParameter2.Value = item;
			if (sqliteCommand5.ExecuteScalar() != null)
			{
				hashSet.Add(item);
			}
		}
		return hashSet;
	}

	private string ContentAnchor(long length)
	{
		using FileStream fileStream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		byte[] array = new byte[(int)Math.Min(65536L, length)];
		fileStream.ReadExactly(array);
		incrementalHash.AppendData(array);
		if (length > array.Length)
		{
			fileStream.Position = Math.Max(array.Length, length - array.Length);
			int length2 = (int)(length - fileStream.Position);
			fileStream.ReadExactly(array.AsSpan(0, length2));
			incrementalHash.AppendData(array.AsSpan(0, length2));
		}
		return Convert.ToHexString(incrementalHash.GetHashAndReset());
	}
}
