using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class CloudflareDnsUpdater
{
    private static readonly string ApiToken = "xvaHxaT-nPRUt495qJ4-izW3wq5cUU-8vGnTVOta";
    private static readonly string ZoneId = "440abee883ddbf2f4670cd22db45e10b";
    private static readonly string[] RecordNames = { "mythmud.com", "www.mythmud.com" };

    private static readonly HttpClient client = new HttpClient();

    public static async Task UpdateIfNeededAsync()
    {
        try
        {
            string currentIp = await GetPublicIp();

            foreach (var recordName in RecordNames)
            {
                try
                {
                    var record = await GetDnsRecord(recordName);

                    if (record == null)
                    {
                        Console.WriteLine($"[DNS] Record not found for {recordName}.");
                        continue;
                    }

                    string dnsIp = record.Value.ip;
                    string recordId = record.Value.id;

                    if (currentIp == dnsIp)
                    {
                        Console.WriteLine($"[DNS] {recordName} IP unchanged ({dnsIp}).");
                        continue;
                    }

                    Console.WriteLine($"[DNS] Updating {recordName} from {dnsIp} → {currentIp}");

                    var body = new
                    {
                        type = "A",
                        name = recordName,
                        content = currentIp,
                        ttl = 1,
                        proxied = true
                    };

                    string json = JsonSerializer.Serialize(body);

                    var request = new HttpRequestMessage(
                        HttpMethod.Put,
                        $"https://api.cloudflare.com/client/v4/zones/{ZoneId}/dns_records/{recordId}"
                    );

                    request.Headers.Add("Authorization", $"Bearer {ApiToken}");
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.SendAsync(request);
                    Console.WriteLine($"[DNS] {recordName} update response: {await response.Content.ReadAsStringAsync()}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DNS] Failed to update {recordName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DNS] Update failed: {ex.Message}");
        }
    }

    private static async Task<string> GetPublicIp()
    {
        return (await client.GetStringAsync("https://api.ipify.org")).Trim();
    }

    private static async Task<(string id, string ip)?> GetDnsRecord(string recordName)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones/{ZoneId}/dns_records?type=A&name={recordName}"
        );

        request.Headers.Add("Authorization", $"Bearer {ApiToken}");

        var response = await client.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("result");

        if (results.GetArrayLength() == 0)
            return null;

        var record = results[0];
        return (
            record.GetProperty("id").GetString(),
            record.GetProperty("content").GetString()
        );
    }
}

