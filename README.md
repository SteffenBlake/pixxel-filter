# PixxelFilter!

This is my proof of concept Loot Filter that utilized my framework tool, PoeFilterX, to dynamically compile loot filters as needed!

Multiple custom maintained NPM packages work together in tandem here to demonstrate the powerful functionality of package/community based loot filter tooling!

If you are curious for information about what PoeFilterX even is, you can start by checking out the tools github here: https://github.com/SteffenBlake/PoeFilterX 

# Whats the point of this project

PixxelFilter, at its core, is mostly meant to serve a couple purposes.

1. As a proof of concept for PoeFilterX's functionality. Largely speaking you can consider this project a good template to look at for if you want to make your own PoeFilterX project, treat it like an example of how I approached organizing the files and setup compilation in a reasonable way, as well as examples of how to make a style package and variable package for other users to consume from

2. As a testing mechanism, I have been using this loot filter for about a league and a half now and it serves as a very handy way for me to test the performance of PoeFilterX as a tool, get an idea of any types of functionality it may need added, and help highlight any UX issues the tool may be lacking on. Largely speaking this project is my sandbox for testing PoeFilterX out and for awhile was simply just my own personal loot filter, which leads me to...

3. Because why not. I mean I already built the loot filter, it certainly works pretty decent now (though requires a lot more polishing), so after all this work it just made sense to release it public just in case anyone else wants to give it a spin. Don't consider this a final production ready loot filter though, it's pretty buggy and I trust it about as far as I can throw it. The Ruthless filter is *definitely* the far more polished filter though so if you really wanna try it out, use that one!

# How to compile this project

Step 1: Ensure you have PoeFilterX installed by following the "Toucan Way" installation instructions here: https://github.com/SteffenBlake/PoeFilterX/wiki/Installation

Step 2: Clone this git repo either using git, or by downloading the raw source code from here: https://github.com/SteffenBlake/PoeFilterX/archive/refs/heads/main.zip and extracting from the zip.

Step 3: Open up your preferred terminal inside of the root folder of the project (Powershell, Terminal, Bash, Cmd, etc)

Step 4: Ensure npm packages are up to date via `npm ci` (only needs to be done once)

Step 5: Execute `npm run build` to compile all the loot filters, they will output to a `./output` folder. The first time you build may take some extra time as the poe ninja data gets cached (or if you havent built the folder in 6 hours, which will retrigger a poe ninja cache)

Step 6: Copy the filter(s) you want to your Path of Exile filter folder https://www.poewiki.net/wiki/Guide:Item_filter_guide#Saving_your_Filter

# (Experimental) PoE2 Filter support

I have a WIP PoE2 filter in this project as well, current testing seems to indicate it is working.

The steps to compile it and use it are largely the same, however the command is `npm run build-poe2` instead

You'll need to copy the filter files to your PoE2 folder instead, of course

In game, go to `Menu > Options > Game > Item Filter` and click the folder icon to the right, this should open up your folder where generated .filter files should be placed

# Modifying the output directory

If you want to automatically compile the filters to your PoE loot filter folder, you can modify your copy of `./package.json`, specifically changing the `--o` arguments for all 6 build scripts values to be the absolute path to the folder.

These build scripts are:

* `build-normal-low`
* `build-normal-med`
* `build-normal-high`
* `build-ruth-low`
* `build-ruth-med`
* `build-ruth-high`

For example, you probably want to change `build-normal-low` to this:

`"build-normal-low": "poefilterx build --p ./Norm-Low.filterx --o \"C:/Users/<YourUserNameHere>/Documents/My Games/Path of Exile/Low.filter"",`

## Package: pixxel-ninja-parser

Github repo documentation: https://github.com/SteffenBlake/pixxel-ninja-parser

PixxelNinjaParser is a handy middleware tool I designed for PoefilterX that parses out Poe.Ninja API price data into a json file that PoeFilterX is capable of consuming. Data is cached to avoid spamming poe ninja servers!

## Package: pixxel-filter-style

Github repo documentation: https://github.com/SteffenBlake/pixxel-filter-style

This is my "core" style sheet for both my Ruthless and Normal Filters. If you want to use the color pallete and icon pallete that PixxelFilter uses without needing to install the entire filter, you can head over to its repo for installation instructions.

The goal of this package is to demonstrate the modularity of PoeFilterX projects. Community maintainers can create color pallete "style sheets" for other users to consume from and incorporate into their own filters. Adding them to your filter should be as easy as a simple `npm install <package name>`!

For more information regarding importing from installed npm packages, see here: https://github.com/SteffenBlake/PoeFilterX/wiki/Code-Guide#node_modules

## Package: pixxel-filter-shortcuts

Github repo documentation: https://github.com/SteffenBlake/pixxel-filter-shortcuts

Though small at the moment (and largely unused in the consuming filter project), the goal of this package is to demonstrate the ability to also modularily share, install, and load custom variables for injection.

Community maintainers can create numerous .json files that provide handy "shorthand" variables that represent numerous values for injecting into your loot filter.

You can see the example here: https://github.com/SteffenBlake/pixxel-filter-shortcuts/blob/main/bases.json

For more information on variable injection, see here: https://github.com/SteffenBlake/PoeFilterX/wiki/Code-Guide#variable-injection
