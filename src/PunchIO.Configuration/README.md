# PunchIO.Configuration

Named per-file profiles for [PunchIO.Core](https://www.nuget.org/packages/PunchIO.Core),
bound from `Microsoft.Extensions.Configuration`.

```json
{
  "PunchIO": {
    "Files": {
      "CustomerMaster": {
        "Format": "VariableBlock",
        "Preset": "Fujitsu",
        "Variable": { "Endianness": "LittleEndian" },
        "Io": { "QueueDepth": 8, "BlockSize": "1MiB" }
      }
    }
  }
}
```

```csharp
services.AddPunchIO(configuration);

var profile = provider.GetRequiredService<IFileProfileProvider>().Get("CustomerMaster");
await using var reader = profile.OpenRead(path);
```

## Two rules worth knowing

**A preset seeds, explicit keys win.** `"Preset": "Fujitsu"` supplies every
field; anything you also write overrides it. Adjusting one aspect of a known
format is a single line.

**Validation happens when the profile resolves, not when a file is read.** Every
failure names the profile and the key:

```
File profile 'CustomerMaster', key 'Variable:Endianness':
  'Sideways' is not valid. Expected one of: BigEndian, LittleEndian.
```

Profiles are built eagerly at registration, so a typo in a profile nobody has
opened yet still fails at startup rather than at byte 40 of a 400 GB file.

Binding is source-generated, not reflective, so this package survives NativeAOT.
Sizes accept unit suffixes (`"1MiB"`); `KB` and `KiB` both mean 1024.

## Licence

MIT. Micro Focus, Fujitsu and NetCOBOL are trademarks of their respective
owners; this project is not affiliated with or endorsed by any of them, and
those names appear only to describe the formats and interfaces it supports.
