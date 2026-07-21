# Third-party notices

## GeoNames worldwide place data

`src/Sharpbill.Infrastructure/Data/rg_cities1000.csv` is the worldwide `cities1000` place dataset
formatted by `reverse_geocoder` 1.5.1. The source data is provided by
[GeoNames](https://www.geonames.org/) under the
[Creative Commons Attribution 4.0 license](https://creativecommons.org/licenses/by/4.0/).
GeoNames attribution: “GeoNames geographical database.”

The formatting implementation that produced this file is from
[`thampiman/reverse-geocoder`](https://github.com/thampiman/reverse-geocoder), licensed under the
GNU Lesser General Public License. Sharpbill embeds the resulting data file but does not copy or
link the Python implementation into the .NET service.

The vendored file's SHA-256 digest is
`1de56dc32b0308c6094d5d833441c8ca25827f24e9a6a4cc144223ab5f9b65bf`.
