# Fuseki image `digiwells/dwis.blackboard.fuseki`

Apache Jena Fuseki with TDB2, the store of the semantic service (decision D-03), built from the Apache Jena Docker build kit pinned to Jena 6.2.0 (decision D-28).

The four files `Dockerfile` (above the marked line), `entrypoint.sh`, `download.sh` and `log4j2.properties` are the build kit `jena-fuseki2/jena-fuseki-docker` of Apache Jena at tag `jena-6.2.0`, unchanged, under the Apache License 2.0 (their headers say so). The build downloads `jena-fuseki-server-6.2.0.jar` from Maven Central and checks its SHA1. To move to a newer Jena, replace the four files with the kit of that tag and change `JENA_VERSION` in `compose/compose.yaml` and the workflows.

Ours: the lines after the marker in `Dockerfile` and `config/dwis.ttl`, which declares the datasets `/dwis` (TDB2, union default graph; endpoints `sparql`, `query`, `update`, `data`, `shacl`) and `/dwis-quarantine` (TDB2). The inference view `/dwis-inf` is added in milestone 2.

Build alone:

```bash
docker build --build-arg JENA_VERSION=6.2.0 -t digiwells/dwis.blackboard.fuseki:dev fuseki
```

The server runs as user `fuseki` (uid 1000) on port 3030; the databases live under `/fuseki/databases`, a volume in the compose file. `JAVA_OPTIONS` sets the heap (the compose file uses 2 GB).
