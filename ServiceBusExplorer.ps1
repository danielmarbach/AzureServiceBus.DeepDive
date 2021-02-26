$url = "https://github.com/paolosalvatori/ServiceBusExplorer/releases/download/5.0.2/ServiceBusExplorer-5.0.2.zip"
$outputFile = "$PSScriptRoot\ServiceBusExplorer.zip"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
(New-Object System.Net.WebClient).DownloadFile($url, $outputFile)
Expand-Archive $outputFile -DestinationPath "$PSScriptRoot\explorer" -Force
Remove-Item $outputFile -Force
$xmlPath = "$PSScriptRoot\explorer\ServiceBusExplorer.exe.Config"
Copy-Item -Path ServiceBusExplorer.exe.Config -Destination $xmlPath -Force
$xml=New-Object XML
$xml.Load($xmlPath)
$nodes = $xml.SelectNodes('/configuration/serviceBusNamespaces/add[@key="ASB_DEEP_DIVE"]');
foreach($node in $nodes) {
    $node.SetAttribute("value", $env:AzureServiceBus_ConnectionString);
}
$xml.Save($xmlPath)