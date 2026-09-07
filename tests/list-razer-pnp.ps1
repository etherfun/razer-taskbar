Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'Razer|Joro' -or $_.InstanceId -match 'VID_1532|vid&0001532' } | ForEach-Object {
    "{0}  [{1}]  {2}" -f $_.Status, $_.Class, $_.FriendlyName
    "    {0}" -f $_.InstanceId
}
