using Azure.Identity;
using Azure.Core;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// ===== CONFIGURATION =====
var subscriptionId = "YOUR_SUBSCRIPTION_ID";
// =========================

var today = DateTime.UtcNow.Date;
var endDate = today.AddDays(-3);
var startDate = today.AddDays(-33);

Console.WriteLine("╔════════════════════════════════════════════════════════╗");
Console.WriteLine("║   Resource Identification Field Analysis              ║");
Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

try
{
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeAzureCliCredential = false,
        ExcludeEnvironmentCredential = true
    });

    var tokenContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
    var token = await credential.GetTokenAsync(tokenContext, default);

    await AnalyzeResourceFields(token.Token, subscriptionId, startDate, endDate);
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ Error: {ex.Message}");
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();

async Task AnalyzeResourceFields(string accessToken, string subId, DateTime start, DateTime end)
{
    var scope = $"/subscriptions/{subId}";
    var apiUrl = $"https://management.azure.com{scope}/providers/Microsoft.CostManagement/generateCostDetailsReport?api-version=2023-11-01";

    Console.WriteLine("Generating cost report...\n");
    
    var payload = new
    {
        metric = "ActualCost",
        timePeriod = new
        {
            start = start.ToString("yyyy-MM-dd"),
            end = end.ToString("yyyy-MM-dd")
        }
    };

    using var apiClient = new HttpClient();
    apiClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

    var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
    request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    
    var response = await apiClient.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        throw new Exception("API call failed");
    }

    var operationUrl = response.Headers.Location?.ToString();
    
    // Poll
    string? downloadUrl = null;
    for (int i = 0; i < 60; i++)
    {
        await Task.Delay(5000);
        var statusResp = await apiClient.GetAsync(operationUrl!);
        var statusJson = await statusResp.Content.ReadAsStringAsync();
        var statusDoc = JsonDocument.Parse(statusJson);
        var status = statusDoc.RootElement.GetProperty("status").GetString();
        
        Console.Write($"\rPolling: {status}     ");
        
        if (status == "Completed")
        {
            downloadUrl = statusDoc.RootElement.GetProperty("manifest")
                .GetProperty("blobs")[0].GetProperty("blobLink").GetString();
            break;
        }
    }
    
    Console.WriteLine("\n\nDownloading CSV...\n");
    
    using var blobClient = new HttpClient();
    var csv = await blobClient.GetStringAsync(downloadUrl!);
    
    var lines = csv.Split('\n');
    var headers = ParseCsvLine(lines[0]);
    
    Console.WriteLine("═══════════════════════════════════════");
    Console.WriteLine("ALL COLUMNS IN CSV");
    Console.WriteLine("═══════════════════════════════════════\n");
    
    for (int i = 0; i < headers.Count; i++)
    {
        Console.WriteLine($"{i,3}: {headers[i]}");
    }
    
    Console.WriteLine("\n═══════════════════════════════════════");
    Console.WriteLine("RESOURCE-RELATED COLUMNS");
    Console.WriteLine("═══════════════════════════════════════\n");
    
    var resourceFields = headers
        .Select((h, i) => new { Index = i, Name = h })
        .Where(x => x.Name.Contains("resource", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("instance", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Contains("service", StringComparison.OrdinalIgnoreCase))
        .ToList();
    
    foreach (var field in resourceFields)
    {
        Console.WriteLine($"{field.Index,3}: {field.Name}");
    }
    
    // Find indices
    var dateIdx = headers.IndexOf("date");
    var costIdx = headers.IndexOf("costInBillingCurrency");
    var consumedServiceIdx = headers.IndexOf("consumedService");
    var meterNameIdx = headers.IndexOf("meterName");
    var resourceIdIdx = headers.IndexOf("resourceId");
    var resourceLocationIdx = headers.IndexOf("resourceLocation");
    var resourceGroupIdx = headers.IndexOf("resourceGroup");
    var instanceIdIdx = headers.IndexOf("instanceId");
    var serviceNameIdx = headers.IndexOf("serviceName");
    var serviceFamilyIdx = headers.IndexOf("serviceFamily");
    
    Console.WriteLine("\n═══════════════════════════════════════");
    Console.WriteLine("SAMPLE OPENAI ENTRY WITH ALL RESOURCE FIELDS");
    Console.WriteLine("═══════════════════════════════════════\n");

    // Find first OpenAI entry and show all its resource-related fields
    for (int i = 1; i < lines.Length; i++)
    {
        if (string.IsNullOrWhiteSpace(lines[i])) continue;
        
        try
        {
            var fields = ParseCsvLine(lines[i]);
            if (fields.Count <= meterNameIdx) continue;

            var meterName = fields[meterNameIdx];
            
            if (meterName.Contains("gpt", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Date: {(dateIdx >= 0 ? fields[dateIdx] : "N/A")}");
                Console.WriteLine($"Cost: {(costIdx >= 0 ? fields[costIdx] : "N/A")}");
                Console.WriteLine($"MeterName: {meterName}");
                Console.WriteLine();
                
                Console.WriteLine("Resource Identification Fields:");
                Console.WriteLine("-------------------------------");
                Console.WriteLine($"resourceId:         '{(resourceIdIdx >= 0 && resourceIdIdx < fields.Count ? fields[resourceIdIdx] : "N/A")}'");
                Console.WriteLine($"resourceGroup:      '{(resourceGroupIdx >= 0 && resourceGroupIdx < fields.Count ? fields[resourceGroupIdx] : "N/A")}'");
                Console.WriteLine($"resourceLocation:   '{(resourceLocationIdx >= 0 && resourceLocationIdx < fields.Count ? fields[resourceLocationIdx] : "N/A")}'");
                Console.WriteLine($"instanceId:         '{(instanceIdIdx >= 0 && instanceIdIdx < fields.Count ? fields[instanceIdIdx] : "N/A")}'");
                Console.WriteLine($"consumedService:    '{(consumedServiceIdx >= 0 ? fields[consumedServiceIdx] : "N/A")}'");
                Console.WriteLine($"serviceName:        '{(serviceNameIdx >= 0 && serviceNameIdx < fields.Count ? fields[serviceNameIdx] : "N/A")}'");
                Console.WriteLine($"serviceFamily:      '{(serviceFamilyIdx >= 0 && serviceFamilyIdx < fields.Count ? fields[serviceFamilyIdx] : "N/A")}'");
                
                Console.WriteLine("\nAll fields for this entry:");
                Console.WriteLine("-------------------------");
                foreach (var field in resourceFields)
                {
                    var value = field.Index < fields.Count ? fields[field.Index] : "N/A";
                    if (!string.IsNullOrEmpty(value))
                    {
                        Console.WriteLine($"  {field.Name}: '{value}'");
                    }
                }
                
                break;
            }
        }
        catch { }
    }
    
    Console.WriteLine("\n═══════════════════════════════════════");
    Console.WriteLine("RECOMMENDATION");
    Console.WriteLine("═══════════════════════════════════════\n");
    
    Console.WriteLine("To correlate with logged API calls, check which of the above");
    Console.WriteLine("fields are populated and contain identifiable information.");
    Console.WriteLine("\nCommon fields for matching:");
    Console.WriteLine("  • resourceId - Full Azure resource ID");
    Console.WriteLine("  • instanceId - Often same as resourceId or VM instance");
    Console.WriteLine("  • resourceGroup - Name of the resource group");
    Console.WriteLine("  • resourceLocation - Azure region (e.g., eastus)");
    Console.WriteLine("\nIf these are empty for global deployments, you may need to:");
    Console.WriteLine("  • Use deployment name from API logs");
    Console.WriteLine("  • Match by timestamp + cost amount");
    Console.WriteLine("  • Use meter name to identify model type");
}

List<string> ParseCsvLine(string line)
{
    var fields = new List<string>();
    var current = new StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (c == '"') inQuotes = !inQuotes;
        else if (c == ',' && !inQuotes)
        {
            fields.Add(current.ToString().Trim());
            current.Clear();
        }
        else current.Append(c);
    }
    fields.Add(current.ToString().Trim());
    return fields;
}