# Declared facts

You know your library better than any website does. **Declared facts** let an admin write down what the works in a folder, or in a whole library, are: their **type** (manga, manhwa, manhua, webtoon, comic, graphic novel or novel - each shown with the country it stands for, for example **Manhwa (Korea)**) and their **creators** (names, each with an optional role). MangaPixer shows them next to the series information and points out when a linked MangaUpdates record says something else.

Declared facts are explicit settings. MangaPixer never guesses them from the name of a library or folder: a folder called `Manhwa` declares nothing until an admin declares it.

## Where they apply

A declaration applies to the folder (or library) and to everything below it, like the folder reading direction. The nearest declaration wins, separately for the type and for the creators:

- A library declared **Manga** makes every folder in it manga, until a folder below declares its own type.
- A folder that declares creators replaces the creators declared above it; the two lists are not combined.
- An archive shows what its folders declare.

Example: the library `Comics` declares **Comic**; its folder `Imports/Korean` declares **Manhwa**; the folder `Imports/Korean/Some Series` declares the creator **A Writer (Story)**. `Some Series` is then a manhwa by A Writer, and every other folder in `Comics` is a comic without declared creators.

## Set them (admins)

- **A folder**: **Admin** > **Declared facts…** in a folder's series panel or on its series page.
- **A library**: **MangaPixer Administration** > **Libraries**: the **Declared:** line under each library shows what the library declares; **Edit** opens the same editor.

In the editor:

1. **Type**: pick one, or leave it at the first entry, which shows what applies from above (for example "Inherit: Manhwa (Korea), from Korean") or **Not set**. The types name their country of origin: **Manga (Japan)**, **Manhwa (Korea)**, **Manhua (China)**, **Webtoon (any country)**, **Comic (Western)**, **Graphic novel (Western)** and **Novel (any country)**. Pick by where the works come from; if you are not sure, leave it unset.
2. **Creators**: type a name, pick a role if you know it (**Story**, **Art**, **Story & art**, or **Any role**) and press **Add** (or Enter). Each creator appears as a chip; remove one with its **×**. Up to 20 creators. While a folder declares no creators, the editor shows the ones it inherits.
3. **Save**. **Clear** removes this folder's or library's own declaration, so what is declared above applies again.

Only admins can see and change the editor. Every change is recorded in the audit trail.

## What you see

The series panel and the series page show a **Declared:** line with the type and the creators; hover a value to see where it comes from ("set here", "from Korean", "from the Comics library"). Everyone who can open the folder sees the line. It is hidden while **Show series information** is off for the library or everywhere.

When the folder is linked to a MangaUpdates series that says otherwise, a **Conflict** badge appears under the line with what MangaUpdates says, so both sides are visible:

- **Type**: manga expects a Japanese record, manhwa a Korean one, manhua a Chinese or Taiwanese one; comic and graphic novel conflict with those three origins; novel conflicts with a comic record and every other type with a novel. A webtoon only conflicts with a novel. A record that does not say where it comes from never conflicts.
- **Creators**: a conflict when none of the declared names is one of the record's names. Names match regardless of word order, capital letters, accents and punctuation (`ODA Eiichiro` is `Eiichiro Oda`), but a different spelling is a different name.

A declaration never changes the linked record, and the record never changes the declaration. If the conflict means the folder is linked to the wrong series, use **Identify…** to link the right one; if your declaration is wrong, edit it.

## Declared facts and automatic matching

With **Automatic matching** on, declared facts are used as evidence. A declared **type** is a strong hint in both directions: records from the country the type names are clearly preferred, and records the type contradicts (a Japanese record for a folder declared manhwa) lose the same amount. That settles a tie between two series with the same name - a Korean webtoon and a Japanese manga both called *Wind Breaker*, for example - but it never beats a record whose title fits the folder better, and it never blocks a link: a folder declared with the wrong type still finds and links its series. The review list shows the evidence on each candidate (**Fits declared type** / **Not declared type**), and **Identify…** warns when the record you preview does not fit the declared type. Declared **creators** prefer records by them (a folder of several same-titled series links the one by the declared author) and never count against a record. See [Automatic matching](series-information.md#automatic-matching).

## Declared facts and Content

The folder **Content** setting (**Doujinshi & adult one-shots**, see [Series information](series-information.md#folder-content)) stays a separate setting with its own menu; it is not a declared fact.

## Privacy

Declared facts are stored in MangaPixer's own database. Reading or changing them makes no request to MangaUpdates or any other site, and no declared fact - type, creators, or the folders they are declared on - is ever sent anywhere; they are only compared, on your server, with what the sites return.

## Good to know

- When a folder is renamed or moved, its declared facts go with it to the new folder, like its link and settings (see [Renamed and moved folders](series-information.md#renamed-and-moved-folders)); a new folder that already has declared facts of its own keeps them, and the old ones wait under **Missing folders**.
- Removing a library from MangaPixer removes its declared facts too.
