# MatchZy — Eventos de Webhook

Este documento describe **todos los eventos** que MatchZy emite hacia un servicio
HTTP externo (típicamente el backend del lobby/stats) y el formato exacto del
JSON que se envía.

---

## 1. Configuración del transporte

Los eventos se envían como **POST HTTP** con `Content-Type: application/json`
hacia la URL definida por el cvar:

```cfg
matchzy_remote_log_url "https://tu-backend.tld/v1/api/match/event"
```

Si querés agregar una cabecera de autenticación (recomendado), usá también:

```cfg
matchzy_remote_log_header_key   "MatchZy-Auth"
matchzy_remote_log_header_value "tu-token-secreto"
```

Esos tres cvars también pueden venir embebidos en el JSON de la match config
bajo `"cvars": { ... }` para configurarlos por partida.

> Si la URL no está definida, los eventos se loggean en consola pero **no se
> envían**.

---

## 2. Campos comunes

Todos los payloads incluyen siempre:

| Campo     | Tipo     | Descripción                                              |
| --------- | -------- | -------------------------------------------------------- |
| `event`   | `string` | Identificador del tipo de evento (ej. `going_live`).     |
| `matchid` | `number` | ID de la partida (el mismo que llegó en la match config). |

Los eventos a nivel mapa agregan:

| Campo        | Tipo     | Descripción                                              |
| ------------ | -------- | -------------------------------------------------------- |
| `map_number` | `number` | Índice del mapa en la serie, base 0 (mapa 1 = `0`).      |

Los eventos a nivel ronda agregan además:

| Campo          | Tipo     | Descripción                                            |
| -------------- | -------- | ------------------------------------------------------ |
| `round_number` | `number` | Número de ronda dentro del mapa (base 1).              |
| `round_time`   | `number` | Tiempo transcurrido de la ronda en segundos (cuando aplica). |

---

## 3. Catálogo de eventos

### `series_start`
Disparado al cargar una match config y arrancar la serie.

```json
{
  "event": "series_start",
  "matchid": 1777681966,
  "team1": { "id": "team1", "name": "TeamA" },
  "team2": { "id": "team2", "name": "TeamB" },
  "num_maps": 3
}
```

### `map_warmup_started` *(custom — Hapore)*
Disparado cuando un mapa de la serie ya cargó y el warmup está activo
(después del `changelevel` y de ejecutar `warmup.cfg`). El backend debería
usar este evento para saber **exactamente cuándo** es seguro empezar a
chequear estados, no antes.

```json
{
  "event": "map_warmup_started",
  "matchid": 1777681966,
  "map_number": 0
}
```

### `player_connected` *(custom — Hapore)*
Se emite cada vez que un jugador esperado por la match config (team1 o team2)
hace SIGNON_FULL. Permite mostrar progreso en la UI del lobby.

```json
{
  "event": "player_connected",
  "matchid": 1777681966,
  "map_number": 0,
  "player_steamid": "76561198170752234",
  "player_name": "Foxx",
  "team": "team2",
  "connected_count": 1,
  "expected_count": 2
}
```

### `all_players_connected` *(custom — Hapore)*
Se emite **una sola vez por mapa** cuando todos los jugadores esperados
están conectados y arranca el countdown de inicio (ver
`matchzy_match_start_countdown`).

```json
{
  "event": "all_players_connected",
  "matchid": 1777681966,
  "expected_count": 2
}
```

### `match_cancelled` *(custom — Hapore)*
Disparado cuando el plugin **cancela la partida por su cuenta** (ej. timeout
de espera de jugadores, ver `matchzy_player_wait_timeout`). Reemplaza al
viejo watchdog que vivía en el backend.

```json
{
  "event": "match_cancelled",
  "matchid": 1777681966,
  "reason": "players_not_connected",
  "missing": ["76561198315829127"]
}
```

Códigos de `reason` actuales:
- `players_not_connected`: timeout esperando jugadores faltantes.

### `going_live`
La cuenta atrás terminó y la partida pasa a estado live (knife / primera ronda).

```json
{
  "event": "going_live",
  "matchid": 1777681966,
  "map_number": 0
}
```

### `round_end`
Fin de ronda con stats agregados de cada equipo.

```json
{
  "event": "round_end",
  "matchid": 1777681966,
  "map_number": 0,
  "round_number": 5,
  "round_time": 78,
  "reason": 9,
  "winner": { "team": "team1", "side": "ct" },
  "team1": { "name": "TeamA", "score": 3, "players": [/* ... */] },
  "team2": { "name": "TeamB", "score": 2, "players": [/* ... */] }
}
```

### `map_result`
Fin de mapa con el resultado final del mapa.

```json
{
  "event": "map_result",
  "matchid": 1777681966,
  "map_number": 0,
  "winner": { "team": "team1", "side": "ct" },
  "team1": { "name": "TeamA", "score": 13, "players": [/* ... */] },
  "team2": { "name": "TeamB", "score": 8,  "players": [/* ... */] }
}
```

### `series_end`
Fin de la serie completa (BO1/BO3/BO5).

```json
{
  "event": "series_end",
  "matchid": 1777681966,
  "time_until_restore": 30,
  "winner": { "team": "team1", "side": "ct" },
  "team1_series_score": 2,
  "team2_series_score": 1
}
```

### `player_disconnect`
Un jugador se desconectó del servidor.

```json
{
  "event": "player_disconnect",
  "matchid": 1777681966,
  "player": 4
}
```

### `map_picked`
Un equipo eligió un mapa durante el veto.

```json
{
  "event": "map_picked",
  "matchid": 1777681966,
  "team": "team1",
  "map_name": "de_anubis",
  "map_number": 0
}
```

### `map_vetoed`
Un equipo vetó un mapa.

```json
{
  "event": "map_vetoed",
  "matchid": 1777681966,
  "team": "team2",
  "map_name": "de_inferno"
}
```

### `side_picked`
Selección de bando para un mapa.

```json
{
  "event": "side_picked",
  "matchid": 1777681966,
  "team": "team1",
  "map_name": "de_anubis",
  "map_number": 0,
  "side": "ct"
}
```

### `demo_upload_ended`
Resultado del upload del demo a `matchzy_demo_upload_url`.

```json
{
  "event": "demo_upload_ended",
  "matchid": 1777681966,
  "map_number": 0,
  "filename": "20260502-0010_1777681966_de_anubis_TeamA_vs_TeamB.dem",
  "success": true
}
```

---

## 4. Orden esperado en una serie BO3 normal

```
series_start
  map_warmup_started        (map_number=0)
    player_connected ...    (1 por jugador)
    all_players_connected
    going_live              (map_number=0)
    round_end × N
    map_result              (map_number=0)
  map_warmup_started        (map_number=1)
    player_connected ...
    all_players_connected
    going_live              (map_number=1)
    round_end × N
    map_result              (map_number=1)
  ...
series_end
demo_upload_ended × num_maps_played
```

Si en cualquier punto se agota `matchzy_player_wait_timeout`, se emite:

```
match_cancelled
```

y la serie termina sin más eventos.

---

## 5. Recomendaciones para el consumidor

- **Idempotencia**: el plugin reintenta envíos en algunos casos. Procesá los
  eventos como idempotentes usando `(matchid, event, map_number, round_number)`
  como clave compuesta cuando aplique.
- **Acks**: el plugin no requiere ningún body en la respuesta; basta con un
  `2xx`. Cualquier `4xx`/`5xx` se loggea en consola del server.
- **`player_connected` puede dispararse 2 veces** para el mismo jugador si
  reconecta (ej. `NETWORK_DISCONNECT_LOOPDEACTIVATE`). Tratá `connected_count`
  como **estado actual**, no como contador incremental.
