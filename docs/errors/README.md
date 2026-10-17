# Remotely error codes

Every error that Remotely shows with a code has a page here. The code is `RMT-` and a number. The text of each entry is in the programs' string files (`Diag_Err_<number>_Title`, `_Cause`, `_Fix`, English and Albanian) and is checked against this list by `DiagnosticsTests`.

Numbers: 1xxx same-network connection, 2xxx handshake, 3xxx server and pairing, 4xxx server admin, 5xxx file transfer, 6xxx setup and invites, 7xxx the Manager's administrator helper, 9xxx anything else. A code is never reused or changed in meaning.

The catalogue and `ErrorCatalogue.Classify` are in `Remotely.Core`. A screen shows a code only if its code calls the catalogue; see `docs/troubleshooting.md` for where that is the case today.

| Code | Meaning | Program |
|---|---|---|
| [RMT-1001](RMT-1001.md) | The token is not valid | App |
| [RMT-1002](RMT-1002.md) | That is an IPv6 address | App |
| [RMT-1003](RMT-1003.md) | The address is not valid | App |
| [RMT-1004](RMT-1004.md) | This PC is not connected to a network | Both |
| [RMT-1005](RMT-1005.md) | This PC has only an IPv6 address | Both |
| [RMT-1006](RMT-1006.md) | No PC answered the search | Both |
| [RMT-1007](RMT-1007.md) | The PC could not be reached | Both |
| [RMT-1008](RMT-1008.md) | The other PC did not accept the token | App |
| [RMT-1009](RMT-1009.md) | The token was tried too often | App |
| [RMT-1010](RMT-1010.md) | The saved PC did not accept this PC | App |
| [RMT-1011](RMT-1011.md) | The other PC did not allow the connection | App |
| [RMT-2001](RMT-2001.md) | The other side did not speak Remotely correctly | App |
| [RMT-2002](RMT-2002.md) | The device that answered is not the expected one | App |
| [RMT-2003](RMT-2003.md) | A signature did not check out | App |
| [RMT-2004](RMT-2004.md) | This device is not trusted by the other PC | App |
| [RMT-3001](RMT-3001.md) | This device is not paired with the server | App |
| [RMT-3002](RMT-3002.md) | An unencrypted server connection was refused | App |
| [RMT-3003](RMT-3003.md) | The server key is missing | App |
| [RMT-3004](RMT-3004.md) | The server and this app are different versions | App |
| [RMT-3005](RMT-3005.md) | The server answered in an unexpected way | App |
| [RMT-3006](RMT-3006.md) | The server refused the pairing code | App |
| [RMT-3007](RMT-3007.md) | Too many attempts | App |
| [RMT-3008](RMT-3008.md) | The server has reached its device limit | App |
| [RMT-3009](RMT-3009.md) | This device was blocked by the server | App |
| [RMT-3010](RMT-3010.md) | That device is offline | App |
| [RMT-3011](RMT-3011.md) | That device cannot be connected to | App |
| [RMT-3012](RMT-3012.md) | The server could not be reached | Both |
| [RMT-3013](RMT-3013.md) | The server address is not valid | Both |
| [RMT-4001](RMT-4001.md) | The admin token cannot be read | Manager |
| [RMT-4002](RMT-4002.md) | The server's admin port cannot be reached | Manager |
| [RMT-4003](RMT-4003.md) | The server refused the request | Manager |
| [RMT-5001](RMT-5001.md) | The file was declined | App |
| [RMT-5002](RMT-5002.md) | The file is too large | App |
| [RMT-5003](RMT-5003.md) | The receive folder is full | App |
| [RMT-5004](RMT-5004.md) | There is not enough free space | App |
| [RMT-5005](RMT-5005.md) | A file with that name already exists | App |
| [RMT-5006](RMT-5006.md) | Access to the file or folder was denied | App |
| [RMT-5007](RMT-5007.md) | The file arrived damaged | App |
| [RMT-5008](RMT-5008.md) | The file or folder was not found | App |
| [RMT-5010](RMT-5010.md) | The file is in use by another program | App |
| [RMT-5011](RMT-5011.md) | The transfer was not understood | App |
| [RMT-5012](RMT-5012.md) | The folder was refused as unsafe | App |
| [RMT-5013](RMT-5013.md) | The other side cannot do this | App |
| [RMT-5014](RMT-5014.md) | The connection dropped during the transfer | App |
| [RMT-5015](RMT-5015.md) | The transfer failed | App |
| [RMT-5016](RMT-5016.md) | The file changed while it was being sent | App |
| [RMT-6001](RMT-6001.md) | The invite is not valid | App |
| [RMT-6002](RMT-6002.md) | That public name cannot be used | Manager |
| [RMT-6003](RMT-6003.md) | Windows refused to change the firewall | Manager |
| [RMT-7001](RMT-7001.md) | A service action failed | Manager |
| [RMT-7002](RMT-7002.md) | The helper was started wrongly | Manager |
| [RMT-7003](RMT-7003.md) | Remotely.Server.exe was not found | Manager |
| [RMT-7004](RMT-7004.md) | Administrator rights are needed | Manager |
| [RMT-7005](RMT-7005.md) | The administrator prompt was declined | Manager |
| [RMT-9001](RMT-9001.md) | Something unexpected went wrong | Both |
