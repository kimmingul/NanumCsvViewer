# SAS 카탈로그 픽스처

`SasCatalogReader`(.sas7bcat 파서, 이슈 #20) 검증용 실파일 픽스처.

- 출처: [Roche/pyreadstat](https://github.com/Roche/pyreadstat) `test_data/sas_catalog/` (Apache-2.0)
- `test_data_{linux,win}.sas7bdat` — SEXA/SEXB 문자 컬럼(값 1/2)에 포맷 `$A`/`$B`가 선언된 데이터
- `test_formats_{linux,win}.sas7bcat` — `$A`/`$B` 포맷의 값 라벨(1→Male, 2→Female) 카탈로그
- 기대 결과(pyreadstat `read_sas7bcat` 대조):
  `$A = {"1": "Male", "2": "Female"}`, `$B = {"2": "Female", "1": "Male"}`
