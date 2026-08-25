# Parser fixtures

Byte-exact copies of the save files from one real Rain World install, taken from
`%USERPROFILE%\AppData\LocalLow\Videocult\Rain World`. They exist so the parser tests run
against real game output instead of hand-written XML.

Each file was copied with `Copy-Item` and no text processing, then checked by SHA-256 against
its source. The `.bin` extension is added on copy so nothing mistakes a fixture for a live save.
Every figure below was measured on the copies in this folder.

Treat these as read-only inputs. A test that needs to write must copy to a temp directory first.

## What the files contain

Each one is a DataContract XML serialization of a `System.Collections.Hashtable`, encoded as
UTF-8 with a BOM (`EF BB BF`). The root element is `ArrayOfKeyValueOfanyTypeanyType` in the
default namespace `http://schemas.microsoft.com/2003/10/Serialization/Arrays`. `Keys` and
`Values` each hold the same number of `anyType` children, paired by index.

Two properties of the format drive most of the parser work:

- The default namespace means element lookup has to be namespace-aware or go by local name.
  An `XDocument.Descendants("Keys")` call with no namespace matches nothing.
- The files are fixed-size, with NUL (`0x00`) bytes padding the space after the closing tag.
  Decoded text has to be truncated at the last `</ArrayOfKeyValueOfanyTypeanyType>` before it
  reaches an XML parser.

## Measured facts

| Fixture | Bytes | BOM | NUL padding | HashSize | Keys |
| --- | ---: | :---: | ---: | ---: | --- |
| `sav2.bin` | 98288 | yes | 28125 | 7 | `save__Backup`, `save` |
| `sav3.bin` | 98304 | yes | 46414 | 7 | `save__Backup`, `save` |
| `exp1.bin` | 824 | yes | 0 | 3 | none |
| `expCore1.bin` | 2735 | yes | 0 | 3 | `core` |
| `online_sav.bin` | 12292 | yes | 876 | 7 | `save__Backup`, `save` |
| `options.bin` | 12286 | yes | 2301 | 7 | `thepit_Sandbox`, `ArenaSetup`, `ArenaOnlineMeadowSetup`, `options` |

In every file the bytes after the closing tag are NUL and nothing else.

## Checksummed values against raw values

Some values carry a 32-character lowercase hex MD5 prefix, where the digest covers the UTF-8
bytes of `payload + SALT` and the payload follows the prefix. Others are stored raw. A parser
decides which by testing that the first 32 characters match `^[0-9a-f]{32}$` **and** that the
recomputed digest matches. A value that fails either test is a raw payload, not a corrupt one.

Verified on these fixtures:

| Fixture | Key | Form | Payload chars |
| --- | --- | --- | ---: |
| `sav2.bin` | `save__Backup` | checksummed, digest verified | 29947 |
| `sav2.bin` | `save` | checksummed, digest verified | 30007 |
| `sav3.bin` | `save__Backup` | checksummed, digest verified | 20068 |
| `sav3.bin` | `save` | checksummed, digest verified | 21085 |
| `online_sav.bin` | `save__Backup` | checksummed, digest verified | 4215 |
| `online_sav.bin` | `save` | checksummed, digest verified | 4215 |
| `expCore1.bin` | `core` | raw | 1089 |
| `options.bin` | `options` | raw | 5082 |
| `options.bin` | `ArenaSetup` | raw | 1043 |
| `options.bin` | `ArenaOnlineMeadowSetup` | raw | 71 |
| `options.bin` | `thepit_Sandbox` | raw | 19 |

The raw payloads use their own separators: `<expC>` in `core`, `<optA>`/`<optB>` in `options`,
`<msuA>`/`<msuB>` in the two arena keys, and `<sbA>`/`<sbB>` in `thepit_Sandbox`.

## Progression payloads

The checksummed `save` and `save__Backup` payloads split into records on `<progDivA>`, and each
record splits into header and body on `<progDivB>`. Headers seen here are the empty string (a
leading record present in every `sav` file), `MISCPROG`, `SAVE STATE`, `MAP_<Slugcat>`, and
`MAPUPDATE_<Slugcat>`.

A `SAVE STATE` body splits into fields on `<svA>`. A field is either `KEY<svB>VALUE` or a bare
`KEY`, where the bare form is a boolean flag such as `HASTHEGLOW`. Useful field keys are
`SAV STATE NUMBER` (the campaign/slugcat id), `CYCLENUM`, `FOOD`, `DENPOS`, and `SEED`.
Devourment mod state also lives in this body, as fields whose key starts with
`DEVOURMENTSTATE` and whose value is `pred<dvD>prey<dvD>status<dvD>food`.

What the `save` payload in each fixture decodes to:

| Fixture | Records | SAVE STATE |
| --- | ---: | --- |
| `sav2.bin` | 10 | White, cycle 17, food 3, den `SU_S04`, seed 8840, 40 fields, 0 devourment entries |
| `sav3.bin` | 8 | White, cycle 9, food 0, den `SU_S04`, seed 5986, 81 fields, 4 devourment entries |
| `online_sav.bin` | 4 | no `SAVE STATE` record at all |

## Edge cases these fixtures cover

- `exp1.bin` parses cleanly but holds zero keys, with empty `Keys` and `Values` and a `HashSize`
  of 3. The expected result is an empty entry set rather than an exception.
- `online_sav.bin` has no `SAVE STATE` record, so campaign detail is absent from a file that is
  otherwise well-formed.
- `expCore1.bin` and `options.bin` hold only raw values, so a parser that assumes every value is
  checksummed fails on them.
- `sav2.bin` and `sav3.bin` carry tens of kilobytes of NUL padding, which is where a missing
  truncation step shows up.
- `sav3.bin` is the only fixture with devourment entries.

## Slot numbering

The UI numbers slots from 1. File `sav` is slot 1, `sav2` is slot 2, `sav3` is slot 3.

The live folder also holds stray files named `sav - Copy` and `sav - Copy (2)` sitting next to
`sav`. File selection has to use an exact-match regex. A `sav*` glob picks up the strays.
