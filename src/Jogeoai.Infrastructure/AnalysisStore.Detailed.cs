using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalysisStore
{
    private async Task WriteDetailedFactsAsync(MySqlConnection connection, MySqlTransaction transaction,
        Lease lease, bool configurationMissing, CancellationToken cancellationToken)
    {
        string prompt, locale, searchJson;
        await using (var check = new MySqlCommand("""
            SELECT prompt_snapshot, locale, search_queries FROM visibility_checks
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
              AND id = UUID_TO_BIN(@check);
            """, connection, transaction))
        {
            AddIds(check, lease);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("待分析 Check 不存在。");
            prompt = reader.GetString(0);
            locale = reader.IsDBNull(1) ? "" : reader.GetString(1);
            searchJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
        }
        var queries = JsonSerializer.Deserialize<string[]>(searchJson) ?? [];
        foreach (var query in DetailedAnalyticsRules.Queries(queries, prompt))
        {
            var hash = DetailHash(query.Key);
            await using (var insert = new MySqlCommand("""
                INSERT INTO check_query_events (release_id, check_id, query_hash, query_key,
                    original_query, first_position, included, exclusion_reason, normalization_version)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, @key,
                    @original, @position, @included, @reason, @version);
                """, connection, transaction))
            {
                AddIds(insert, lease);
                insert.Parameters.AddWithValue("@hash", hash);
                insert.Parameters.AddWithValue("@key", BoundQueryText(query.Key));
                insert.Parameters.AddWithValue("@original", BoundQueryText(query.Original));
                insert.Parameters.AddWithValue("@position", query.Position);
                insert.Parameters.AddWithValue("@included", query.Included);
                insert.Parameters.AddWithValue("@reason", query.ExclusionReason);
                insert.Parameters.AddWithValue("@version", DetailedAnalyticsRules.QueryVersion);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!query.Included) continue;
            foreach (var term in DetailedAnalyticsRules.CompareTerms(query.Original, prompt, locale) ?? [])
            {
                var text = term.Text.Length > 256 ? term.Text[..256] : term.Text;
                await using var insert = new MySqlCommand("""
                    INSERT INTO query_term_facts (release_id, check_id, query_hash, term_hash,
                        term, relation, tokenizer_version)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @queryHash, @termHash,
                        @term, @relation, @version);
                    """, connection, transaction);
                AddIds(insert, lease);
                insert.Parameters.AddWithValue("@queryHash", hash);
                insert.Parameters.AddWithValue("@termHash", DetailHash(text));
                insert.Parameters.AddWithValue("@term", text);
                insert.Parameters.AddWithValue("@relation", term.Relation);
                insert.Parameters.AddWithValue("@version", DetailedAnalyticsRules.TokenizerVersion);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        var configuration = SubjectConfiguration.Parse(lease.SiteId, lease.ConfigVersion, lease.Snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var citations = new MySqlCommand("""
            SELECT normalized_url, title, position FROM citations
            WHERE check_id = UUID_TO_BIN(@check) ORDER BY position, id;
            """, connection, transaction);
        citations.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        var raw = new List<(string Url, string? Title, int Position)>();
        await using (var reader = await citations.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                raw.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2)));
        foreach (var item in raw)
        {
            var url = DetailedAnalyticsRules.NormalizeUrl(item.Url, lease.UrlVersion);
            if (url is null || !seen.Add(url)) continue;
            var hash = DetailHash(url);
            var google = DetailedAnalyticsRules.GoogleInternal(url, item.Title, item.Position, configuration);
            if (google is not null)
            {
                await using var insert = new MySqlCommand("""
                    INSERT INTO google_internal_events (release_id, check_id, url_hash, normalized_url,
                        kind, search_query, product, availability, ownership, subject_id, first_position, rule_version)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, @url,
                        @kind, @query, @product, @availability, @ownership, UUID_TO_BIN(@subject), @position, @version);
                    """, connection, transaction);
                AddIds(insert, lease);
                insert.Parameters.AddWithValue("@hash", hash);
                insert.Parameters.AddWithValue("@url", google.Url);
                insert.Parameters.AddWithValue("@kind", google.Kind);
                insert.Parameters.AddWithValue("@query", google.Query);
                insert.Parameters.AddWithValue("@product", google.Product);
                insert.Parameters.AddWithValue("@availability", google.Availability);
                insert.Parameters.AddWithValue("@ownership", google.Ownership);
                insert.Parameters.AddWithValue("@subject", google.SubjectId?.ToString());
                insert.Parameters.AddWithValue("@position", google.FirstPosition);
                insert.Parameters.AddWithValue("@version", DetailedAnalyticsRules.ClassificationVersion);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }
            // Classify performs release normalization itself. Passing the already
            // normalized URL would strip a second www prefix and disagree with its hash.
            var classified = DetailedAnalyticsRules.Classify(item.Url, item.Title, item.Position, configuration, lease.UrlVersion);
            await using var external = new MySqlCommand("""
                INSERT INTO citation_events (release_id, check_id, url_hash, normalized_url,
                    domain, title, first_position, url_version, normalization_status)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, @url,
                    @domain, @title, @position, @version, 'complete');
                INSERT INTO citation_classifications (release_id, check_id, url_hash, source_category,
                    page_type, ownership, subject_id, rule_version, rule_basis)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, @category,
                    @pageType, @ownership, UUID_TO_BIN(@subject), @ruleVersion, @basis);
                """, connection, transaction);
            AddIds(external, lease);
            external.Parameters.AddWithValue("@hash", hash);
            external.Parameters.AddWithValue("@url", classified.Url);
            external.Parameters.AddWithValue("@domain", classified.Domain);
            external.Parameters.AddWithValue("@title", classified.Title);
            external.Parameters.AddWithValue("@position", classified.FirstPosition);
            external.Parameters.AddWithValue("@version", lease.UrlVersion);
            external.Parameters.AddWithValue("@category", classified.SourceCategory);
            external.Parameters.AddWithValue("@pageType", classified.PageType);
            external.Parameters.AddWithValue("@ownership", configurationMissing ? "unknown" : classified.Ownership);
            external.Parameters.AddWithValue("@subject", configurationMissing ? null : classified.SubjectId?.ToString());
            external.Parameters.AddWithValue("@ruleVersion", DetailedAnalyticsRules.ClassificationVersion);
            external.Parameters.AddWithValue("@basis", configurationMissing ? "configuration_missing" : classified.RuleBasis);
            await external.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string DetailHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string BoundQueryText(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 2048));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == 2048) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
