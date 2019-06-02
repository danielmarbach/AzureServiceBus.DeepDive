$url = "https://github.com/paolosalvatori/ServiceBusExplorer/releases/download/4.1.112/ServiceBusExplorer-4.1.112.zip"
$outputFile = "$PSScriptRoot\ServiceBusExplorer.zip"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
(New-Object System.Net.WebClient).DownloadFile($url, $outputFile)
Expand-Archive $outputFile -DestinationPath "$PSScriptRoot\explorer" -Force
Remove-Item $outputFile -Force