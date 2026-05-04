#include <amxmodx>
#include <amxmisc>
#include <reapi>
#include <sockets>
#include <cstrike>
#include <engine>


#define DEBUG 0
#define FUNC_TRACE 0
#define DEBUG_SNAPSHOT 0
#define DEBUG_SNAPSHOT_VERBOSE 0

#define SOCKET_TASK_ID 1

#define SNAPSHOT_TASK_ID 2
#define SNAPSHOT_TICK 0.20

#define POS_EPSILON_XY_SQ 32.0 // Threshold to send new XY position.
#define POS_EPSILON_Z  8.0 // Threshold to send new Z position.
#define YAW_EPSILON 3.0 // Threshold to send yaw.

#define MAX_EVENTS 64
#define EVT_MAX_DATA 2
#define EVT_TYPE 0
#define EVT_DATA1 1
#define EVT_DATA2 2

// Index of the buffer where the player packet starts ([P] tag).
// Write player packet tag.
// Index of the buffer where the player count will be written.
// Initialize player count to 0.
#define BEGIN_PLAYER_PACKET() \
	players_start_idx = len; \
	buffer[len++] = _:PCKT_PLAYERS; \
	player_count_idx = len++; \
	buffer[player_count_idx] = 0

#define DISCARD_PLAYER_PACKET() \
	len = players_start_idx

#define PLAYER_COUNT() \
	buffer[player_count_idx]

#define COMMIT_PLAYER() \
	write_u16(buffer, flags_idx, flags); \
	buffer[player_count_idx]++

#define BEGIN_PLAYER() \
	player_idx = len; \
	buffer[len++] = i; \
	flags_idx = len; \
	len += 2; \
	flags = 0

#define DISCARD_PLAYER() \
	len = player_idx

// Packs first 2 chars into a 16-bit key for fast switch.
#define WKEY(%1,%2) ((%1) | ((%2) << 8))

#if DEBUG_SNAPSHOT
new const g_player_flags_names[][] = {
	"TEAM", "YAW", "POS", "HP", "ARMOR", "CURWEP", 
	"MONEY", "FRAGS", "DEATHS", "INV", "ITEMS", "NAME", "DROPPED"
};

new const g_global_flags_names[][] = {
	"ROUND TIME", "SCORE", "MAP"
};

new const g_global_events_names[][] = {
	"ROUND ENDED", "PL DIED",
	"BOMB PLANTING", "EVT_BOMB_PLANT_ABORTED", "BOMB PLANTED",
	"BOMB DROPPED", "BOMB PICKED UP",
	"EVT_BOMB_DEFUSING", "EVT_BOMB_DEFUSE_ABORTED"
};
#endif

#if FUNC_TRACE

new const g_msg_dest_name[][] = {
	"MSG_BROADCAST",        // Unreliable to all
	"MSG_ONE",              // Reliable to one (msg_entity)
	"MSG_ALL",              // Reliable to all
	"MSG_INIT",             // Write to the init string
	"MSG_PVS",              // Ents in PVS of org
	"MSG_PAS",              // Ents in PAS of org
	"MSG_PVS_R",            // Reliable to PVS
	"MSG_PAS_R",            // Reliable to PAS
	"MSG_ONE_UNRELIABLE",   // Send to one client, but don't put in reliable stream, put in unreliable datagram (could be dropped)
	"MSG_SPEC",
	"UNKNOWN"
};

new g_msg_name[255][32];

#endif

enum PacketType
{
	PCKT_GLOBAL = 'G',
	PCKT_PLAYERS = 'P',
	PCKT_EVENTS = 'E'
};

// ORGANIZED FROM MOST TO LEAST
// FREQUENTLY UPDATED.
enum SnapshotGlobalFlags
{
	GF_NONE = 0,
	DF_ROUND_TIME = 1 << 0,
	DF_SCORE	  = 1 << 1,
	DF_MAP		  = 1 << 2
};

// ORGANIZED FROM MOST TO LEAST
// FREQUENTLY UPDATED, WITH
// ONE EXCEPTION.
enum SnapshotPlayerFlags
{
	PF_NONE = 0,
	
	// Send if dead or alive.
	DF_TEAM    = 1 << 0,
	DF_NAME    = 1 << 1,
	DF_MONEY   = 1 << 2,
	DF_FRAGS   = 1 << 3,
	DF_DEATHS  = 1 << 4,
	
	// Send only if alive.
	DF_YAW	   = 1 << 5,
	DF_POS	   = 1 << 6,
	DF_HP	   = 1 << 7,
	DF_ARMOR   = 1 << 8,
	DF_CURWEP  = 1 << 9,
	DF_INV	   = 1 << 10,
	DF_ITEMS   = 1 << 11,
	
	DF_DROPPED = 1 << 12 // Special flag to indicate player just dropped, so we don't need to check any other flag or data for that player.
};

enum EventType
{
	EVT_ROUND_ENDED,
	EVT_DIED, // killer, victim, weapon, flashed?

	EVT_BOMB_PLANTING,
	EVT_BOMB_PLANT_ABORTED,
	EVT_BOMB_PLANTED,

	EVT_BOMB_DROPPED,
	EVT_BOMB_PICKED_UP,

	EVT_BOMB_DEFUSING,
	EVT_BOMB_DEFUSE_ABORTED,

	EVT_BOMB_DEFUSED,
	EVT_BOMB_EXPLODED,

	EVT_FLASHED, // EVENT_PLAYER_BLINDED_BY_FLASHBANG
	EVT_KILL_FLASHBANGED,

	EVT_PLAYER_JOINED,
	EVT_PLAYER_DROPPED
}

new g_socket; // The socket.

// Map name and name length.
// This is basically free and set
// upon opening the socket sucessfully.
// Exanpansive to check at every snapshot tick.
new g_map_name[32], g_map_name_len;

// Track players that just joined the server
// and need to be handled in a special way.
new bool:g_player_pending_join[MAX_CLIENTS + 1];

// Track players that left the server
// and need to be handled in a special way.
new bool:g_player_pending_drop[MAX_CLIENTS + 1];

// Holds names upon connection/changes.
// Why? Expansive to keep checking every snapshot tick.
new g_cached_names[MAX_CLIENTS + 1][32];

// Holds current teams upon joining/changing.
// Why? Expansive to keep checking every snapshot tick.
new TeamName:g_cached_teams[MAX_CLIENTS + 1];

// Holds the time that the current round should end
// considering get_gametime() as the starting point.
// Time left: g_round_end_time - get_gametime().
new Float:g_round_end_time;

// Holds current scores.
// Why? Doesn't change a lot, so we avoid repetitive
// checks inside the snapshot tick.
new g_t_score, g_ct_score,
bool:g_planting_bomb, bool:g_bomb_planted, bool:g_defusing_bomb,
bool:g_bomb_is_on_ground;

// These hold what has changed in global state
// and data per player so we know what to send for each one.
new SnapshotPlayerFlags:g_player_dirty[33];
new SnapshotGlobalFlags:g_global_dirty;

new g_events[MAX_EVENTS][1 + EVT_MAX_DATA];
new g_event_count;
new const g_event_size[] =
{
	1, // EVT_ROUND_ENDED
	4, // EVT_DIED

	1, // EVT_BOMB_PLANTING
	1, // EVT_BOMB_PLANT_ABORTED
	1, // EVT_BOMB_PLANTED

	8, // EVT_BOMB_DROPPED
	1, // EVT_BOMB_PICKED_UP

	1, // EVT_BOMB_DEFUSING
	1, // EVT_BOMB_DEFUSE_ABORTED

	1, // EVT_BOMB_DEFUSED
	1, // EVT_BOMB_EXPLODED

	1, // EVT_FLASHED
	1, // EVT_KILL_FLASHBANGED
};

#if DEBUG_SNAPSHOT
new g_dbg_buffer[2048];
new g_dbg_buffer_len;
#endif

logdbg(message[], any: ...)
{
#if DEBUG
	static buffer[2048];
	vformat(buffer, charsmax(buffer), message, 2);
	log_amx("%s", buffer);
#endif

#if !DEBUG
	#pragma unused message
#endif
}

public plugin_init()
{
	#if FUNC_TRACE
	log_amx("plugin_init()");
	build_msg_name_table();
	#endif

	register_plugin("LocalStreamer", "1.0", "VictorOak");

	// Send basic global info right away.
	g_global_dirty = DF_MAP | DF_SCORE | DF_ROUND_TIME;

	prepare_socket();
}

public plugin_end()
{
	#if FUNC_TRACE
	log_amx("plugin_end()");
	#endif

	if (g_socket > 0)
		socket_close(g_socket);
}

public client_putinserver(id)
{
	#if FUNC_TRACE
	log_amx("client_putinserver(id<%d>)", id);
	#endif

	g_player_pending_drop[id] = false;
	g_player_pending_join[id] = true;
}

public client_disconnected(id, bool:drop, message[], maxlen)
{
	#if FUNC_TRACE
	log_amx("client_disconnected(id<%d>, drop<%s>, message<^"%s^">, maxlen<%d>)",
		id,
		drop ? "true" : "false",
		message,
		maxlen);
	#endif

	g_player_pending_join[id] = false;
	g_player_pending_drop[id] = true;
}

public client_remove(id)
{
	#if FUNC_TRACE
	log_amx("client_remove(id<%d>)", id);
	#endif
}

public client_infochanged(id)
{
	#if FUNC_TRACE
	log_amx("client_infochanged(id<%d>)", id);
	#endif

	// Runs during user connection, after client_putinserver
	// and after changing teams (because of the model).
	// This erratic behavior makes it challenging to find the
	// ideal place to reset the name cache. However, since it
	// always runs during connection, we use that because
	// the user is not in the server yet.
	
	// log_amx("client_infochanged (connecting = %d; connected = %d)", is_user_connecting(id), is_user_connected(id));

	if (!is_user_connected(id)) {
		g_cached_names[id][0] = 0;
		return;
	}

	/*
	Possible "info keys":
	_pw <string>
	_cl_autowepswitch <1|0>
	bottomcolor <int>
	cl_dlmax <int>
	cl_lc <1|0>
	cl_lw <1|0>
	cl_updaterate <int>
	topcolor <int>
	_vgui_menus <1|0>
	_ah <1|0>
	_snd_mic <guid>
	rate <int>
	*sid <int> (steam id)
	name <string>
	model <string> (terror, arctic, leet, guerilla | urban, gsg9, sas, gign)
	*/
	// new ib = engfunc(EngFunc_GetInfoKeyBuffer, id);
	// new out[2048];
	// copy_infokey_buffer(ib, out, charsmax(out));
	// log_amx("info buffer: %s", out);

	// Since this function is called before player
	// is actually connected, cs_get_user_team
	// throws an error, so we check this here.
	//if (!is_user_connected(id))
		//return;

	new new_name[32];
	get_user_info(id, "name", new_name, charsmax(new_name));

	if (!equal(new_name, g_cached_names[id]))
	{
		copy(g_cached_names[id], charsmax(g_cached_names[]), new_name);
		g_player_dirty[id] |= DF_NAME;
	}	
}

// RegisterHookChain
public round_started() 
{
	#if FUNC_TRACE
	log_amx("round_started()");
	#endif

	new current_t_scores  = get_member_game(m_iNumTerroristWins);
	new current_ct_scores = get_member_game(m_iNumCTWins);

	// Same scores.
	if (current_t_scores == g_t_score && current_ct_scores == g_ct_score)
		return;

	// Update scores.
	g_t_score = current_t_scores;
	g_ct_score = current_ct_scores;

	g_global_dirty |= DF_SCORE;
}

// RegisterHookChain
public round_ended(WinStatus:status, ScenarioEventEndRound:event, Float:time_to_new_round)
{
	#if FUNC_TRACE
	log_amx("round_ended(<WinStatus>%d, <EvtEndRnd>%d, <TimeToNewRnd>%.02f)",
		status, event, time_to_new_round);
	#endif

	// status: WINSTATUS_NONE = 0, WINSTATUS_CTS, WINSTATUS_TERRORISTS, WINSTATUS_DRAW -> requires 2 bits
	// event: event that triggered the end (19 options) -> requires 5 bits
	// time_to_new_round: delay before new round starts -> not used
	// Total bits needed = 7 (~1 byte)
	//
	// [0000 00][00]
	//		   ^--- 2 bits for status (values 0, 1, 2, 3)
	//  ^------- 6 free bits for event, but only 5 needed
	//			(values 0 to 18 - enough for the 19 possible values)
	//
	// status & 0x3 to match the right-most 2 bits
	// event & 0x1F because 1F is [0001 1111], then << 2 to use the bits to the left of the status

	new byte = (_:status) | (_:event << 2);
	push_event(EVT_ROUND_ENDED, byte);

	g_planting_bomb = g_defusing_bomb = g_bomb_planted = g_bomb_is_on_ground = false;
}

// register_message
public roundtime_changed(msg_id, msg_dest, id)
{
	#if FUNC_TRACE
	log_msg_fnc("roundtime_changed", msg_id, msg_dest, id);
	#endif

	g_round_end_time = get_gametime() + float(get_msg_arg_int(1));
}

// register_event_ex
public team_changed()
{
	#if FUNC_TRACE
	log_amx("team_changed()");
	#endif

	new id, team_c[2];
	new TeamName:team;

	id = read_data(1);

	// log_amx("team_changed (connecting = %d; connected = %d)", is_user_connecting(id), is_user_connected(id));

	// This message is triggered:
	// -before user connection (before client_putinserver);
	// -during connection (is_user_connecting = 1);
	// -after user disconnected (after client_disconnected, with is_user_connected = 0);
	// -and before user removed (client_remove).
	if (!is_user_connected(id) || is_user_connecting(id)) {
		// If we got here, it is because this message was
		// captured either after the user disconnected or
		// during connection. Either way, we leverage
		// both situations to reset the cache.
		// Why not only is_user_connected? Because then
		// we would not get the package on first connection.
		g_cached_teams[id] = TEAM_UNASSIGNED;
		g_player_dirty[id] |= DF_TEAM;
		return;
	}

	// data[2] is either "UNASSIGNED", "TERRORIST", "CT" or "SPECTATOR".
	// Read only the first char.
	read_data(2, team_c, charsmax(team_c));

	switch (team_c[0])
	{
		case 'U': team = TEAM_UNASSIGNED;
		case 'T': team = TEAM_TERRORIST;
		case 'C': team = TEAM_CT;
		case 'S': team = TEAM_SPECTATOR;
		default: team = TEAM_UNASSIGNED;
	}

	// log_amx("team changed attmp for %d from %d to %d", id, g_cached_teams[id], team);
	// Check the flag as well because since the default value is UNASSIGNED,
	// the flag indicates that the value is dirty (cache was reset).
	if (g_cached_teams[id] != team || (g_player_dirty[id] & DF_TEAM)) {
		// log_amx("[TEAM CHANGED!!!] player %d changed TEAMS from %d to %d", id, g_cached_teams[id], team);
		g_cached_teams[id] = team;
		g_player_dirty[id] |= DF_TEAM;
	}
}

// register_message
public death_msg(msg_id, msg_dest, id)
{
	#if FUNC_TRACE
	log_msg_fnc("death_msg", msg_id, msg_dest, id);
	#endif

	static weapon[32];

	new killer = get_msg_arg_int(1);
	new victim = get_msg_arg_int(2);
	// new headshot = get_msg_arg_int(3);
	
	// Truncated weapon name: without "weapon_".
	// HE = grenade, not hegrenade.
	get_msg_arg_string(4, weapon, charsmax(weapon));
	new wep_id = _:weapon_to_id(weapon);

	new assistant = 0;
	new rarity = 0;

	new arg_c = get_msg_args();

	// Using ReGameDLL custom arguments
	// via #define REGAMEDLL_ADD.
	if (arg_c > 4) {

		new flags = get_msg_arg_int(5);
		new idx = 6; // // If message does not contain coords where victim died, next possible index for assistant is 6.

		if (flags & _:PLAYERDEATH_POSITION)
			idx = 9; // Because args 6 through 8 are coords where player died, so assitant index is 9.

		if (flags & _:PLAYERDEATH_ASSISTANT) {
			assistant = get_msg_arg_int(idx++); // Increment index of argument for kill rarity.
		}

		if (flags & _:PLAYERDEATH_KILLRARITY) {
			rarity = get_msg_arg_int(idx);
		}
	}

	// killer id    = 6 bits (0 to 32)
	// victim id    = 5 bits (0 to 31 -> id - 1)
	// assistant id = 6 bits (0 means no assist, 1 to 32 the id)
	// weapon id    = 5 bits
	// rarity       = 10 bits (KillRarity has 10 values)
	// total        = 32 bits = 4 bytes.
	new data = (killer & 0x3F)
		| ((victim - 1 & 0x1F) << 6)
		| ((assistant & 0x3F) << 11)
		| ((wep_id & 0x1F) << 17)
		| ((rarity & 0x3FF) << 22);

	push_event(EVT_DIED, data);

	// log_amx("k %d, v %d, a %d, w %d, rarity %d, data %d", killer, victim, assistant, wep_id, rarity, data);
}

// register_message
public bar_time_msg(msg_id, msg_dest, id)
{
	#if FUNC_TRACE
	log_msg_fnc("bar_time_msg", msg_id, msg_dest, id);
	#endif

	new duration = get_msg_arg_int(1);

	// Message to hide the bar but no one was either planting or defusing.
	// This message is fired to clear hud sometimes so we need this guard.
	if (duration == 0 && !(g_planting_bomb || g_defusing_bomb))
		return;

	// log_amx("BarTime: msg %d dest %d id %d duration %d", msg_id, msg_dest, id, duration);
	
	switch(g_cached_teams[id])
	{
		case TEAM_TERRORIST:
		{
			g_planting_bomb = !g_planting_bomb;

			if (g_planting_bomb) {
				push_event(EVT_BOMB_PLANTING, id);

				#if DEBUG
				log_amx("PLANTING");
				#endif
			}
			else {
				if (!g_bomb_planted) {
					push_event(EVT_BOMB_PLANT_ABORTED, id);

					#if DEBUG
					log_amx("PLANTING ABORTED");
					#endif
				}
			}
		}
		case TEAM_CT:
		{
			g_defusing_bomb = !g_defusing_bomb;

			if (g_defusing_bomb) {
				push_event(EVT_BOMB_DEFUSING, id);

				#if DEBUG
				log_amx("DEFUSING");
				#endif
			}
			else {
				push_event(EVT_BOMB_DEFUSE_ABORTED, id);

				#if DEBUG
				log_amx("DEFUSING ABORTED");
				#endif
			}
		}
	}
}

// register_message
public bomb_drop_msg(msg_id, msg_dest, id)
{
	#if FUNC_TRACE
	log_msg_fnc("bomb_drop_msg", msg_id, msg_dest, id);
	#endif

	new drop_type = get_msg_arg_int(4);

	if (drop_type == 1) {
		// Fired before the BarTime message.
		g_bomb_planted = true;

		push_event(EVT_BOMB_PLANTED, id);

		#if DEBUG
		log_amx("BOMB PLANTED");
		#endif
	}
	else {

		if (g_bomb_is_on_ground)
			return;

		g_bomb_is_on_ground = true;

		new drop_x = floatround(get_msg_arg_float(1));
		new drop_y = floatround(get_msg_arg_float(2));
		new drop_z = floatround(get_msg_arg_float(3));

		// For this event, we need 6 bits for the id (1 to 32)
		// and another 6 bytes (48 bits) for x, y and z,
		// totaling 54 bits, which requires a minimum
		// of 7 bytes (2 cells).
		// first cell: [0000 0000][xxxx xxxx][xxxx xxxx][0000 0000]
		//                                                 ^-- up to 6th bit, id; from the second byte/8th bit onwards, we store the X coord. Still 1 byte left.
		push_event(EVT_BOMB_DROPPED, (id) | ((drop_x) << 8), (drop_y) | ((drop_z) << 16));

		#if DEBUG
		log_amx("BOMB DROPPED");
		#endif
	}
}

// register_message
public bomb_pickup_msg(msg_id, msg_dest, id)
{
	#if FUNC_TRACE
	log_msg_fnc("bomb_pickup_msg", msg_id, msg_dest, id);
	#endif

	if (msg_dest != MSG_ONE) {
		// log_amx("BOMB PICKUP != MSG_ONE: %d", msg_dest);
		return;
	}
	
	g_bomb_is_on_ground = false;

	push_event(EVT_BOMB_PICKED_UP, id);

	#if DEBUG
	log_amx("BOMB PICKED UP");
	#endif
}

// From csx.
// Might generate duplicate because
// a bomb defuse will also trigger a RoundEndEvent
// with an event of ROUND_BOMB_DEFUSED.
public bomb_defused(const id)
{
	#if FUNC_TRACE
	log_amx("bomb_defused(id<%d>)", id);
	#endif

	push_event(EVT_BOMB_DEFUSED, id);
}

prepare_socket()
{
	remove_task(SOCKET_TASK_ID);
	remove_task(SNAPSHOT_TASK_ID);
	set_task_ex(3.0, "open_socket", SOCKET_TASK_ID, _, _, SetTask_Repeat);
}

public open_socket()
{
	#if FUNC_TRACE
	log_amx("open_socket()");
	#endif

	new error;

	if (g_socket > 0) {
		socket_close(g_socket);
		g_socket = 0;
	}

	g_socket = socket_open("127.0.0.1", 37015, SOCKET_UDP, error, SOCK_NON_BLOCKING);
		
	switch (error)
	{
		case 0:
		{
			remove_task(SOCKET_TASK_ID);

			// Initialize globals.
			g_map_name_len = get_mapname(g_map_name, charsmax(g_map_name));

			// Update score
			RegisterHookChain(RG_CSGameRules_RestartRound, "round_started", 1);
			RegisterHookChain(RG_RoundEnd, "round_ended", 1);

			register_event_ex("TeamInfo" , "team_changed", RegisterEvent_Global);

			// Sends the clock new time and starts countdown from it to 0.
			register_message(get_user_msgid("RoundTime"), "roundtime_changed");
			register_message(get_user_msgid("DeathMsg"), "death_msg");
			register_message(get_user_msgid("BarTime"), "bar_time_msg");
			register_message(get_user_msgid("BombDrop"), "bomb_drop_msg");
			register_message(get_user_msgid("BombPickup"), "bomb_pickup_msg");

			set_task_ex(SNAPSHOT_TICK, "send_snapshot", SNAPSHOT_TASK_ID, _, _, SetTask_Repeat);
			
			log_amx("Socket ready");
			return;
		}
		case 1:
		{
			logdbg("Error while creating socket");
			return;
		}
		case 2:
		{
			logdbg("Couldn't resolve hostname");
			return;
		}
		case 3:
		{
			logdbg("Couldn't connect");
			return;
		}
	}
}

public send_packet(data[], length)
{
	#if FUNC_TRACE
	log_amx("send_packet(data<%s>, length<%d>)", data, length);
	#endif

	new bytes_sent = socket_send2(g_socket, data, length);
	new send_success = bytes_sent != -1;
	
	#if DEBUG

	/*
	static mimic_failure = 0;
	static mimic_failure_max = 7;
	mimic_failure++;
	if (mimic_failure % mimic_failure_max == 0) {
		mimic_failure = 0;
		mimic_failure_max++;
		if (mimic_failure_max % 12 == 0)
			mimic_failure_max = 3;
		return -1;
	}
	*/

	new byte_str[1024];

	if (byte_arr_to_str(byte_str, charsmax(byte_str), data, length) != -1)
		log_amx("Sending %04d bytes - [%s]", length, byte_str);

	if (byte_arr_to_str(byte_str, charsmax(byte_str), data, length, 0, true) != -1)
		log_amx("                     [%s]", byte_str);

	if (!send_success) {
		log_amx("Socket send failed!^n");
	}
	else {
		log_amx("Sent <%d> bytes!^n", bytes_sent);
	}

	#endif

	return send_success;
}


#if DEBUG_SNAPSHOT
stock bool:append_dbf(const fmt[], any:...)
{
    new remaining = charsmax(g_dbg_buffer) - g_dbg_buffer_len;

    if (remaining <= 0)
        return false;

    new written = vformat(
        g_dbg_buffer[g_dbg_buffer_len],
        remaining,
        fmt,
        2
    );

    if (written <= 0)
        return false;

    g_dbg_buffer_len += written;
    return true;
}
#endif


bool:build_global_packet(Float:tick_now, buffer[], &len, max_len)
{
	#if FUNC_TRACE
	log_amx("build_global_packet(tick_now<%.02f>, buffer<[...]>, len<%d>, max_len<%d>)", tick_now, len, max_len);
	#endif

	// [G] packet
	// [G][global flags][data]
	// global flags: 1 byte (u8);
	// global flags data size:
	//  -ROUND_TIME = 4 bytes (f32 - 4 bytes float);
	//  -SCORE      = 2 bytes (u16) (t score, ct score);
	//  -MAP        = first byte is name length (u8);
	//
	// Theoretical maximum size: 1 (packet type) + 1 (flags) + 4 (round time) + 2 (score) + 1 + 32 (map name) = 41 bytes.

	if (len + 2 > max_len) {
		// Not enough space to write packet type and count, retry on next snapshot.
		return false;
	}

	new SnapshotGlobalFlags:written_flags = GF_NONE;

	new start = len;

	// Add global packet tag.
	buffer[len++] = _:PCKT_GLOBAL;
	new flags_idx = len++;
	
	#if DEBUG_SNAPSHOT

	new dbg_bf[256];
	new dbg_bf_len = 0;	

	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[GLOBAL][");
	dbg_bf_len += format_flags(_:g_global_dirty,
		g_global_flags_names, sizeof(g_global_flags_names),
		dbg_bf,
		charsmax(dbg_bf), dbg_bf_len);
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "]");

	#endif

	if (g_global_dirty & DF_ROUND_TIME && (len + 4 <= max_len)) {

		// If round hasn't started yet, the global is zero.
		// Round might not have started due to lack of players, etc...

		write_f32(buffer, len, g_round_end_time > tick_now ? g_round_end_time : 0.0);
		written_flags |= DF_ROUND_TIME;

		#if DEBUG_SNAPSHOT
		if (g_round_end_time < tick_now)
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[ROUND END <NOT STARTED (end tick is %.02f)>]", g_round_end_time);
		else
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[ROUND END AT TICK <%.02f (%.02f secs left)>]", g_round_end_time, g_round_end_time - tick_now);
		#endif
	}

	if (g_global_dirty & DF_SCORE && (len + 2 <= max_len)) {

		buffer[len++] = g_t_score;
		buffer[len++] = g_ct_score;
		written_flags |= DF_SCORE;

		#if DEBUG_SNAPSHOT
		dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[SCORE <T %d x %d CT (R %d)>]", g_t_score, g_ct_score, g_t_score + g_ct_score);
		#endif
	}

	// Make sure name can fit in buffer;
	// otherwise try again on next tick.
	// It should always fit since buffer is
	// mostly empty at this point, containing
	// only other global data.
	// "+1" because we store strlen
	// bytes + 1 byte for the length itself.
	if (g_global_dirty & DF_MAP && (len + 1 + g_map_name_len <= max_len)) {

		buffer[len++] = g_map_name_len;
		new idx = 0; // current index.
		
		while (idx < g_map_name_len)
			buffer[len++] = g_map_name[idx++];

		written_flags |= DF_MAP;
		
		#if DEBUG_SNAPSHOT
		dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[MAP <%s>]", g_map_name);
		#endif		
	}
	// else { retry on next snapshot... }
	
	if (written_flags == GF_NONE) {
		// Nothing was written, so we don't need to write the packet.
		len = start; // Reset length to before we started writing this packet.
		return false;
	}

	// Add the global flag to the buffer.
	buffer[flags_idx] = _:written_flags;

	// Clear the global dirty flag for the flags we just wrote.
	g_global_dirty &= ~written_flags;
	
	#if DEBUG_SNAPSHOT
	append_dbf("%s", dbg_bf);
	#endif

	return true;
}

bool:build_events_packet(buffer[], &len, max_len)
{
	#if FUNC_TRACE
	log_amx("build_events_packet(buffer<[...]>, len<%d>, max_len<%d>)", len, max_len);
	#endif

	// [E] packet
	// [E][count][type][data][type][data][type][data][type][data]...
	// count: 1 byte (u8);
	// type: 1 byte (u8);

	if (len + 2 > max_len) {
		// Not enough space to write packet type and count, retry on next snapshot.
		return false;
	}

	new start = len;

	// Add global packet tag.
	buffer[len++] = _:PCKT_EVENTS;
	buffer[len++] = 0; // Initialize count to 0.
	
	#if DEBUG_SNAPSHOT

	new dbg_bf[256];
	new dbg_bf_len = 0;	
	new dbg_bf_count_idx;
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[EVENTS][");
	dbg_bf_count_idx = dbg_bf_len;
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "00");
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "]");

	#endif

	new events_written = 0;
	new type;

	for (new i = 0; i < g_event_count; i++) {

		type = g_events[i][EVT_TYPE];

		// +1 to count type plus data size.
		if (len + g_event_size[type] + 1 > max_len) {
			break;
		}

		buffer[len++] = type;

		switch (type)
		{
			case EVT_DIED:
			{
				write_u32(buffer, len, g_events[i][EVT_DATA1]);
			}
			case EVT_BOMB_DROPPED:
			{
				write_u32(buffer, len, g_events[i][EVT_DATA1]);
				write_u32(buffer, len, g_events[i][EVT_DATA2]);
			}
			default:
			{
				buffer[len++] = g_events[i][EVT_DATA1];
			}
		}

		// Increase number of written events.
		events_written++;

		#if DEBUG_SNAPSHOT
		if (type == _:EVT_BOMB_DROPPED) {
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[%s <%d><%d>]", g_global_events_names[type], g_events[i][EVT_DATA1], g_events[i][EVT_DATA2]);
		}
		else {
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[%s <%d>]", g_global_events_names[type], g_events[i][EVT_DATA1]);
		}
		#endif
	}

	if (events_written == 0) {

		// Reset length to before we started writing this packet.
		len = start;

		#if DEBUG_SNAPSHOT
		if (g_event_count > 0) {
			log_amx("No events were written (buffer full).");
		}
		#endif

		return false;
	}

	buffer[start + 1] = events_written;
	
	// All events written.
	if (events_written == g_event_count) {
		g_event_count = 0;
		#if DEBUG_SNAPSHOT
		log_amx("All <%d> events were written in the buffer.", events_written);
		#endif
	}
	else {
		
		#if DEBUG_SNAPSHOT
		log_amx("A total of <%d out of %d> events were written in the buffer.", events_written, g_event_count);
		#endif

		// Not all events were written.
		// Push not written events to the front of
		// the event buffer and adjust the count.
		new events_left = g_event_count - events_written;
		
		for (new i = 0; i < events_left; i++) {
			g_events[i][EVT_TYPE]  = g_events[events_written + i][EVT_TYPE];
			g_events[i][EVT_DATA1] = g_events[events_written + i][EVT_DATA1];
			g_events[i][EVT_DATA2] = g_events[events_written + i][EVT_DATA2];
		}

		g_event_count = events_left;
	}

	#if DEBUG_SNAPSHOT
	dbg_bf[dbg_bf_count_idx] = '0' + (events_written / 10);
	dbg_bf[dbg_bf_count_idx + 1] = '0' + (events_written % 10);
	append_dbf("%s", dbg_bf);
	#endif

	return true;
}

public send_snapshot()
{
	#if FUNC_TRACE
	log_amx("send_snapshot()");
	#endif

	#if DEBUG_SNAPSHOT
	static dbg_bf[2048];
	new dbg_bf_len = 0;
	#endif

	// The packet buffer.
	static buffer[4096];

	// Periodic staggered full refresh.
	// One slice of players per interval.
	static Float:last_refresh_time = 0.0;
	static refresh_phase = 0;

	// Caches (no need to polute global namespace).
	static Float:last_global_written = 0.0;
	static bool:death_pkt_sent[MAX_CLIENTS + 1];

	// Players data cache.
	static prev_yaw[MAX_CLIENTS + 1]; // 1 byte
	static prev_pos[MAX_CLIENTS + 1][3] // 6 bytes
	static prev_hp[MAX_CLIENTS + 1]; // 1 byte
	static prev_armor[MAX_CLIENTS + 1]; // 1 byte
	static prev_curwep[MAX_CLIENTS + 1]; // 1 byte
	static prev_money[MAX_CLIENTS + 1]; // 2 bytes
	static prev_frags[MAX_CLIENTS + 1]; // 1 byte
	static prev_deaths[MAX_CLIENTS + 1]; // 1 byte
	static prev_inv[MAX_CLIENTS + 1]; // 4 bytes
	static prev_items[MAX_CLIENTS + 1]; // 1 byte

	// With this config, 19+32(name)+1(team)+1(id)+2(flags)=55 bytes per player, MAX.
	static const MAX_BYTES_PLAYER_PCKT = 55; /*considering a name change*/

	static const PLAYER_REFRESH_PHASES = 4;
	
	// Holds players position and yaw (horizontal angle)
	static position[3], Float:angles[3];

	new Float:tick_now = get_gametime();

	// Current number of bytes inserted into buffer.
	new len = 0;

	// Important buffer logic variables.
	new tick_idx, players_start_idx, player_idx, player_count_idx, flags_idx, flags, is_alive;

	// Aux to aaoid derreferencing expansive arrays.
	new prev_x, prev_y, prev_z;

	// Auxiliary, not context bound, variables.
	new temp, temp2;

	// Where tick will live.
	// Present in all sent packages.
	tick_idx = len;
	len += 4; // "tick" takes 4 bytes.

	#if DEBUG_SNAPSHOT
	// Reset global snapshot debug buffer.
	g_dbg_buffer_len = 0;
	// Append tick before anything else.
	append_dbf("[TICK %.02f]|", tick_now);
	#endif

	/////////////////////////////////////
	// Add global data
	// Format is: [G][global flags][data]
	/////////////////////////////////////

	// Force send global data per pre-defined timeout.
	// Done just for synchronicity sake.
	if (tick_now - last_global_written > 10.0) {
		g_global_dirty = DF_ROUND_TIME | DF_SCORE | DF_MAP;

		#if DEBUG_SNAPSHOT_VERBOSE
		log_amx("Forcing global package after timeout...");
		#endif
	}
	#if DEBUG_SNAPSHOT_VERBOSE
	else {
		log_amx("No global timeout yet: now is %.02f, last sent is %.02f, diff is %.02f.", tick_now, last_global_written, tick_now - last_global_written);
	}
	#endif

	if (g_global_dirty) {

		// Attempts to append a global packet to the snapshot buffer.
		// Returns true if anything was written.
		// On failure or no-op, buffer and len remain unchanged.
		if ((build_global_packet(tick_now, buffer, len, sizeof(buffer)))) {
			last_global_written = tick_now;
		}
		#if DEBUG_SNAPSHOT
		else {
			log_amx("Not enough space to write global data, retrying on next tick...");
		}
		#endif
	}
	#if DEBUG_SNAPSHOT_VERBOSE
	else {
		log_amx("No dirty global data yet...");
	}
	#endif

	/////////////////////////////////////////////////////////////////////
	// Add players
	// Format is: [P][player count][player id][player flags][player data]
	//                             [player id][player flags][player data]
	//                             ...
	/////////////////////////////////////////////////////////////////////

	// Every 5 seconds refresh another slice.
	if (tick_now - last_refresh_time >= 5.0) {

		last_refresh_time = tick_now;

		// 8 slices total.
		// Players 1,9,17,25 on phase 0
		// Players 2,10,18,26 on phase 1
		// etc.
		for (new i = 1; i <= MAX_CLIENTS; i++) {

			if (!is_user_connected(i))
				continue;

			if ((i - 1) % PLAYER_REFRESH_PHASES != refresh_phase)
				continue;

			g_player_dirty[i] |=
				DF_TEAM |
				DF_NAME;

			// Always-valid stats.
			// These can still change while dead.
			g_player_dirty[i] |=
				DF_MONEY |
				DF_FRAGS |
				DF_DEATHS;

			// Alive-only stats.
			if (is_user_alive(i)) {
				g_player_dirty[i] |=
					DF_YAW |
					DF_POS |
					DF_HP |
					DF_ARMOR |
					DF_CURWEP |
					DF_INV |
					DF_ITEMS;
			}
		}

		refresh_phase++;

		if (refresh_phase >= PLAYER_REFRESH_PHASES)
			refresh_phase = 0;

		#if DEBUG_SNAPSHOT
		log_amx("Periodic staggered refresh phase <%d>", refresh_phase);
		#endif
	}

	BEGIN_PLAYER_PACKET();

	#if DEBUG_SNAPSHOT
	new dbg_bf_pre_player_idx = dbg_bf_len;
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[PLAYERS][COUNT<");
	new dbg_bf_player_count_idx = dbg_bf_len;
	dbg_bf_len += 2; // Reserve 2 chars for player count, which we will fill later.
	dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, ">]");
	#endif

	for(new i = 1; i <= MAX_CLIENTS; i++) {

		BEGIN_PLAYER();
		
		if (g_player_pending_drop[i]) {

			// This means the player just dropped.
			// Let the front end know what happened
			// and deal with it as they see fit.

			// [id][flags] = 3 bytes.
			if (len + 3 > sizeof(buffer)) {

				DISCARD_PLAYER();

				// break because if we can't fit this dropped player,
				// we won't be able to fit any other player either,
				// so no point in continuing the loop.
				break;
			}			

			g_player_pending_drop[i] = false;
			flags = _:DF_DROPPED; // Only relevant flag is dropped, but we set it like this for readability.

			COMMIT_PLAYER();

			#if DEBUG_SNAPSHOT
			dbg_bf[dbg_bf_player_count_idx]     = '0' + (buffer[player_count_idx] / 10);
			dbg_bf[dbg_bf_player_count_idx + 1] = '0' + (buffer[player_count_idx] % 10);
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[id <%d>][DROPPED]", i);
			print_player_buffer(buffer, len, player_idx, _:DF_DROPPED);
			#endif
			
			continue;
		}

		if (g_player_pending_join[i]) {

			// This means the player just joined.
			// In this case, team is probably UNESPECIFIED
			// and no data is really relevant other
			// than team and name.
			
			// Make sure name can fit in buffer;
			// otherwise try again on next tick.
			temp = strlen(g_cached_names[i]); // Holds name length.

			// [id][flags][team][len][name]
			// +5 = id (1 byte) + flags (2 bytes) + name len (1 byte) + team (1 byte)
			if (len + 5 + temp > sizeof(buffer)) {

				DISCARD_PLAYER();

				// Use continue here because the next player
				// processing might actually take less space
				// than this one, so we give it a chance instead
				// of flat out breaking like we do for dropped players.
				continue;
			}
			
			g_player_pending_join[i] = false;
			g_player_pending_drop[i] = false;

			prev_yaw[i] = 0;
			prev_pos[i][0] = 0;
			prev_pos[i][1] = 0;
			prev_pos[i][2] = 0;
			prev_hp[i] = 0;
			prev_armor[i] = 0;
			prev_curwep[i] = 0;
			prev_money[i] = 0;
			prev_frags[i] = 0;
			prev_deaths[i] = 0;
			prev_inv[i] = 0;
			prev_items[i] = 0;
			death_pkt_sent[i] = false;

			g_player_dirty[i] = PF_NONE;

			// Only relevant flags are team and name.
			flags = _:DF_TEAM | _:DF_NAME; 

			buffer[len++] = _:g_cached_teams[i]; // Write team.
			buffer[len++] = temp; // Write name length.
			temp2 = 0; // current index.
				
			// Write name itself.
			while (temp2 < temp)
				buffer[len++] = g_cached_names[i][temp2++];

			COMMIT_PLAYER();

			#if DEBUG_SNAPSHOT
			dbg_bf[dbg_bf_player_count_idx]     = '0' + (buffer[player_count_idx] / 10);
			dbg_bf[dbg_bf_player_count_idx + 1] = '0' + (buffer[player_count_idx] % 10);
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[id <%d>][JOINED][TEAM <%d>][NAME <%s>]", i, g_cached_teams[i], g_cached_names[i]);
			print_player_buffer(buffer, len, player_idx, _:DF_TEAM | _:DF_NAME);
			#endif

			// No further processing needed at this point.
			continue;
		}

		if(!is_user_connected(i)) {
			DISCARD_PLAYER();
			continue;
		}

		// TODO - maybe move this check to the middle of
		// packaging to avoid overflow and optimize
		// for packing, but I want to optimize for performance
		// so this will remain here as to avoid any processing
		// if whole player data won't fit in the buffer.
		if (len + MAX_BYTES_PLAYER_PCKT > sizeof(buffer)) {
			DISCARD_PLAYER();
			break;
		}

		is_alive = is_user_alive(i);

		//////////////////////////////////////////////////
		/// IMPORTANT:
		/// Serialization order MUST match flag bit order.
		/// Do not reorder packet writes independently.
		//////////////////////////////////////////////////

		#if DEBUG_SNAPSHOT
		dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[id <%d>]", i);
		#endif

		if (g_player_dirty[i] & DF_TEAM) {

			temp = _:g_cached_teams[i]; // Current team.

			buffer[len++] = temp;
			flags |= _:DF_TEAM;
			g_player_dirty[i] &= ~DF_TEAM;

			#if DEBUG_SNAPSHOT
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[TEAM <%d>]", temp);
			#endif
		}

		// NAME: [len][name]
		if (g_player_dirty[i] & DF_NAME) {

			// Make sure name can fit in buffer;
			// otherwise try again on next tick.
			temp = strlen(g_cached_names[i]); // name_len.

			// "+1" because we store strlen
			// bytes + 1 byte for the length itself.
			if (len + temp + 1 <= sizeof(buffer)) {
				
				buffer[len++] = temp;
				temp2 = 0; // current index.
				
				while (temp2 < temp)
					buffer[len++] = g_cached_names[i][temp2++];

				flags |= _:DF_NAME;
				g_player_dirty[i] &= ~DF_NAME;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[NAME <%s>]", g_cached_names[i]);
				#endif
			}
			// else { try to fit on next snapshot }
		}

		// MONEY PACKET: [xx] (2 bytes)
		// 2 bytes max = 65535.
		// Not interested in broadcasting a server
		// that has money above 16000 anyway...
		temp = cs_get_user_money(i);
		if (temp != prev_money[i]) {
			prev_money[i] = temp;
			write_u16(buffer, len, temp);
			flags |= _:DF_MONEY;

			#if DEBUG_SNAPSHOT
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[MONEY <%d>]", temp);
			#endif
		}

		// FRAGS PACKET: [x] (1 byte)
		temp = get_user_frags(i);
		if (temp != prev_frags[i]) {
			prev_frags[i] = temp;
			buffer[len++] = temp;
			flags |= _:DF_FRAGS;

			#if DEBUG_SNAPSHOT
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[FRAGS <%d>]", temp);
			#endif
		}

		// DEATHS PACKET: [x] (1 byte)
		temp = cs_get_user_deaths(i);
		if (temp != prev_deaths[i]) {

			if (prev_deaths[i] < temp) {
				// This means the player died since last snapshot.
				death_pkt_sent[i] = false;
			}

			prev_deaths[i] = temp;
			buffer[len++] = temp;
			flags |= _:DF_DEATHS;

			#if DEBUG_SNAPSHOT
			dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[DEATHS <%d>]", temp);
			#endif
		}

		// These are only relevant if player is alive.
		if (is_alive || death_pkt_sent[i] == false) {

			death_pkt_sent[i] = true;

			// YAW PACKET: [x] (1 byte)
			entity_get_vector(i, EV_VEC_v_angle, angles);
			temp = encode_yaw_i8(angles[1]);
			if (should_send_yaw(temp, prev_yaw[i])) {
				prev_yaw[i] = temp;			
				buffer[len++] = temp;
				flags |= _:DF_YAW;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[YAW <%.2ff -> %d>]", angles[1], temp);
				#endif
			}

			// POSITION PACKET: [xx][yy][zz] (6 bytes)
			// Positions are sent as signed 16-bit (two's complement).
			// TODO - maybe send each coord DELTA instead of new values, saving a couple of bytes.
			get_user_origin(i, position);
			prev_x = prev_pos[i][0];
			prev_y = prev_pos[i][1];
			prev_z = prev_pos[i][2];

			if (should_send_pos(
				position[0], position[1], position[2],
				prev_x, prev_y, prev_z)) {

				prev_pos[i][0] = position[0];
				prev_pos[i][1] = position[1];
				prev_pos[i][2] = position[2];

				write_u16(buffer, len, position[0]);
				write_u16(buffer, len, position[1]);
				write_u16(buffer, len, position[2]);

				flags |= _:DF_POS;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[POS <(%d,%d,%d)>]", position[0], position[1], position[2]);
				#endif
			}

			// HP PACKET: [x] (1 byte)
			temp = get_user_health(i);
			if (temp != prev_hp[i]) {
				prev_hp[i] = temp;
				buffer[len++] = temp;
				flags |= _:DF_HP;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[HP <%d>]", temp);
				#endif
			}

			// ARMOR PACKET: [x] (1 byte)
			//   vvv vvvv = armor value bits (7)
			// [0000 0000]
			//  ^-- = armor type bit (8th, being 1 = vesthelm, 0 = vest)
			temp = cs_get_user_armor(i, CsArmorType:temp2);
			// If user armor has any value,
			// then we use the 8th bit to
			// set the armor type.
			// We don't need 3 values (none, vest, vesthelm)
			// because you can't have "none" with a value
			// greater than zero.
			if (temp > 0 && CsArmorType:temp2 == CS_ARMOR_VESTHELM) {
			temp |= (1 << 7);
			}
			// else no need to shift 0 by 7 bits (for CS_ARMOR_VEST),
			// or armor value is 0 and nothing needs to be done either.

			if (temp != prev_armor[i]) {
				prev_armor[i] = temp;
				buffer[len++] = temp;
				flags |= _:DF_ARMOR;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[ARMOR <%s/%d>]", temp2 == _:CS_ARMOR_VESTHELM ? "vesthelm" : "vest", temp);
				#endif
			}

			// CURRENT WEAPON PACKET: [x] (1 byte)
			temp = get_user_weapon(i);
			if (temp != prev_curwep[i]) {
				prev_curwep[i] = temp;
				buffer[len++] = temp;
				flags |= _:DF_CURWEP;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[CURWEP <%d>]", temp);
				#endif
			}

			// INVENTORY PACKET: [xxxx] (4 bytes)
			temp = get_entvar(i, var_weapons);
			if (temp != prev_inv[i]) {
				prev_inv[i] = temp;
				write_u32(buffer, len, temp & 0x7FFFFFFF); // Clear 31st bit.
				flags |= _:DF_INV;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[INV <%d>]", temp & 0x7FFFFFFF);
				#endif
			}

			// ITEMS PACKET: [x] (1 byte)
			// Not using NVG for now.
			temp = cs_get_user_defuse(i);
			if (temp != prev_items[i]) {
				prev_items[i] = temp;
				buffer[len++] = temp;
				flags |= _:DF_ITEMS;

				#if DEBUG_SNAPSHOT
				dbg_bf_len += formatex(dbg_bf[dbg_bf_len], charsmax(dbg_bf) - dbg_bf_len, "[ITEMS/KIT <%d>]", temp);
				#endif
			}

		}

		// Only write the player to the buffer
		// if we actually added anything
		// about this player to the buffer.
		// Otherwise just restore len.
		if (flags == 0) {
			// Restore len to starting position
			// for the next player data.
			// No data changed → discard this player entry entirely.
			DISCARD_PLAYER();
		}
		else {

			// Write this player flags.
			// Increment number of players in the buffer.
			COMMIT_PLAYER();		

			#if DEBUG_SNAPSHOT
			print_player_buffer(buffer, len, player_idx, flags);
			#endif
		}
	}

	if (PLAYER_COUNT() == 0) {
		
		DISCARD_PLAYER_PACKET();

		#if DEBUG_SNAPSHOT
		dbg_bf_len = dbg_bf_pre_player_idx;
		dbg_bf[dbg_bf_len] = 0;
		#endif

		#if DEBUG_SNAPSHOT_VERBOSE
		log_amx("NO PLAYER DATA ADDED - RESETTING BUFFER LENGTH TO %d FROM %d", len, len + 2);
		#endif
	}

	/////////////
	// Add events
	/////////////
	build_events_packet(buffer, len, sizeof(buffer));

	// Only send if anything has be written after tick.
	if (len > tick_idx + 4) {

		write_f32(buffer, tick_idx, tick_now);
		send_packet(buffer, len);

		#if DEBUG_SNAPSHOT
		
		// Patch buffer player count.
		if (PLAYER_COUNT() > 0) {
			dbg_bf[dbg_bf_player_count_idx]     = '0' + (buffer[player_count_idx] / 10);
			dbg_bf[dbg_bf_player_count_idx + 1] = '0' + (buffer[player_count_idx] % 10);
		}

		append_dbf("%s", dbg_bf);

		new byte_str[1024];
		
		log_amx("");
		log_amx("===<WHOLE BUFFER SENT>===");
		
		if (byte_arr_to_str(byte_str, charsmax(byte_str), buffer, len) != -1)
			log_amx("Buffer (%04d bytes): [%s]", len, byte_str);
		
		if (byte_arr_to_str(byte_str, charsmax(byte_str), buffer, len, 0, true) != -1)
			log_amx("              ASCII: [%s]", byte_str);
		
		log_amx("     [DEBUG BUFFER]: %s", g_dbg_buffer);
		
		log_amx("===</WHOLE BUFFER SENT>===");
		log_amx("");
		#endif
	}
	#if DEBUG_SNAPSHOT_VERBOSE
	else {
		log_amx("EMPTY BUFFER. SKIPPING SEND.");
	}
	#endif
}

bool:should_send_pos(x, y, z, prev_x, prev_y, prev_z)
{
	if (x == prev_x && y == prev_y && z == prev_z)
		return false;

	new dx = x - prev_x;
	new dy = y - prev_y;

	if ((dx*dx + dy*dy) > POS_EPSILON_XY_SQ)
		return true;

	return abs(z - prev_z) > POS_EPSILON_Z;
}

bool:should_send_yaw(yaw, prev_yaw)
{
	// Done this way to avoid calling abs() function.
	new diff = yaw - prev_yaw;

	// Correct for circular motion:
	// Depending on the start and end points, the angle might jump
	// from 127 (let's say 180deg) to -127 (think -180).
	// The raw diff is -254, but in reality the player
	// only moved ~2deg. The math below is done to adjust for this
	// to avoid jitter.
	// Trade-off: a long rotation can be interpreted as a short one in the opposite direction,
	// but will still preserve reality of where the player if looking at.
	if (diff > 127) diff -= 256;
	else if (diff < -127) diff += 256;

	return (diff > YAW_EPSILON || diff < -YAW_EPSILON);
}

WeaponIdType:weapon_to_id(const weapon[])
{
	if (!weapon[0] || !weapon[1])
		return WEAPON_NONE;

	new key = weapon[0] | (weapon[1] << 8);

	switch (key)
	{
		// Most frequent ones.
		case WKEY('m','4'): return WEAPON_M4A1;
		case WKEY('a','k'): return WEAPON_AK47;
		case WKEY('g','a'): return WEAPON_GALIL;
		case WKEY('a','w'): return WEAPON_AWP;
		case WKEY('u','s'): return WEAPON_USP;
		case WKEY('g','l'): return WEAPON_GLOCK18;
		case WKEY('d','e'): return WEAPON_DEAGLE;
		case WKEY('c','4'): return WEAPON_C4;
		case WKEY('k','n'): return WEAPON_KNIFE;
		case WKEY('g','r'): return WEAPON_HEGRENADE; // grenade (only exception)
		case WKEY('s','c'): return WEAPON_SCOUT;
		
		case WKEY('a','u'): return WEAPON_AUG;
		case WKEY('e','l'): return WEAPON_ELITE;
		case WKEY('f','i'): return WEAPON_FIVESEVEN;
		case WKEY('f','a'): return WEAPON_FAMAS;
		case WKEY('g','3'): return WEAPON_G3SG1;
		case WKEY('m','a'): return WEAPON_MAC10;
		case WKEY('m','p'): return WEAPON_MP5N;
		case WKEY('m','2'): return WEAPON_M249;
		case WKEY('m','3'): return WEAPON_M3;
		case WKEY('p','2'): return WEAPON_P228;
		case WKEY('p','9'): return WEAPON_P90;
		case WKEY('s','g'):
		{
			// sg550 ou sg552
			return (weapon[3] == '0') ? WEAPON_SG550 : WEAPON_SG552;
		}
		case WKEY('t','m'): return WEAPON_TMP;
		case WKEY('u','m'): return WEAPON_UMP45;
		case WKEY('x','m'): return WEAPON_XM1014;

		case WKEY('s','m'): return WEAPON_SMOKEGRENADE;
		case WKEY('f','l'): return WEAPON_FLASHBANG;
	}

	return WEAPON_NONE;
}

bool:push_event(EventType:type, data1 = 0, data2 = 0)
{
	if (g_event_count >= MAX_EVENTS) {
		#if DEBUG
		log_amx("Event overflow! Dropping event %d", type);
		#endif
		return false;
	}

	g_events[g_event_count][EVT_TYPE]  = _:type;
	g_events[g_event_count][EVT_DATA1] = data1;
	g_events[g_event_count][EVT_DATA2] = data2;
	g_event_count++;

	return true;
}

encode_yaw_i8(Float:yaw)
{
	// -179 to 180.
	// Since there is no need for precision
	// in our case, let's map the 360 degrees
	// to a 256 numbers space, meaning
	// about 360/256 ~= 1.4 degree step for each degree.
	// So how many "steps" our yaw requires?
	// num_steps = yaw/(360/256) = yaw*256/360 -> [-127, 128].
	// yaw = num_steps*360/256 -> back to 2 bytes space, with minimal loss.
	// Since there is possibility to generate +128 (which does not exist
	// in single byte space), we use 127/180 to map 180 numbers into 127 spaces,
	// thus guaranteeing -127 to +127.
	// decode: yaw = byte * 180 / 127.
	new byte_yaw = floatround(yaw * 127 / 180); // Encode in sbyte space.

	if (byte_yaw > 127) byte_yaw = 127;
	else if (byte_yaw < -127) byte_yaw = -127;

	return byte_yaw;
}

// Writes little-endian.
// All multi-byte writes are raw bit copies (no sign semantics).
write_u16(buffer[], &idx, value)
{
	buffer[idx++] = value & 0xFF;
	buffer[idx++] = (value >> 8) & 0xFF;
}

// Writes little-endian.
// All multi-byte writes are raw bit copies (no sign semantics).
write_u32(buffer[], &idx, value)
{
	buffer[idx++] = value & 0xFF;
	buffer[idx++] = (value >> 8) & 0xFF;
	buffer[idx++] = (value >> 16) & 0xFF;
	buffer[idx++] = (value >> 24) & 0xFF;
}

// Writes little-endian.
// All multi-byte writes are raw bit copies (no sign semantics).
write_f32(buffer[], &idx, Float:value)
{
	write_u32(buffer, idx, _:value);
}

#if DEBUG_SNAPSHOT

stock print_player_buffer(buffer[], len, player_idx, flags)
{
	new byte_str[1024];
	
	log_amx("");
	log_amx("===<PLAYER BUFFER>===");
	
	if (byte_arr_to_str(byte_str, charsmax(byte_str), buffer, len, player_idx) != -1)
		log_amx("Player snapshot (%02d bytes): [%s]", len, byte_str);

	if (byte_arr_to_str(byte_str, charsmax(byte_str), buffer, len, player_idx, true) != -1)
		log_amx("                     ASCII: [%s]", byte_str);
	
	print_curr_player_flags(flags);

	log_amx("===</PLAYER BUFFER>===");
	log_amx("");
}

stock byte_arr_to_str(output[], maxlen, data[], length, start_index = 0, bool:use_ascii = false)
{
	if (maxlen < length) {
		logdbg("byte_arr_to_str: output is too small to fit data[] length: %d vs %d", maxlen, length);
		return -1;
	}

	new len = 0;

	if (!use_ascii) {
		for (new i = start_index; i < length; i++) {
			len += formatex(output[len], maxlen - len, "0x%02X ", data[i]);
		}
	}
	else {
		for (new i = start_index; i < length; i++) {
			len += formatex(
				output[len],
				maxlen - len,
				33 <= data[i] <= 126 ? "%c " : "0x%02X ",
				data[i]);
		}
	}

	// Trim last space.
	output[--len] = 0;
	return len;
}

stock byte_arr_to_num(data[], length)
{
	new num = 0, offset = 0;

	for (new i = 0; i < length; i++) {
		num |= (data[i] << offset);
		offset += 8;
	}
	
	return num;
}

print_curr_player_flags(flags)
{
	new szBuffer[512], iLen = 0;
	
	if (flags == 0) {
		log_amx("Flags: NONE");
		return;
	}

	// Percorre os 12 bits definidos no seu enum
	for (new i = 0; i < sizeof(g_player_flags_names); i++) {
		if (flags & (1 << i)) {
			// Adiciona o nome da flag ao buffer, separando por "|"
			iLen += formatex(szBuffer[iLen], charsmax(szBuffer) - iLen, "%s%s", 
				(iLen > 0) ? " | " : "", g_player_flags_names[i]);
		}
	}

	log_amx("                     Flags: [%s]", szBuffer);
}

format_flags(flags, const name_map[][], name_count, out_buffer[], buffer_len, start_index = 0)
{
	new str_len = start_index;
	new bool:first = true;

	for (new i = 0; i < name_count; i++) {

		if (flags & (1 << i)) {

			if (str_len >= buffer_len)
				break;

			str_len += formatex(
				out_buffer[str_len],
				buffer_len - str_len,
				"%s%s",
				first ? "" : "|",
				name_map[i]
			);

			first = false;
		}
	}

	return str_len - start_index;
}

#endif

#if FUNC_TRACE

log_msg_fnc(fnc[], msg_id, msg_dest, id)
{
	log_amx("%s(msg_id<%d/%s>, msg_dest<%d/%s>, id<%d>)",
		fnc,
		msg_id, g_msg_name[msg_id],
		msg_dest, g_msg_dest_name[msg_dest],
		id);
}

build_msg_name_table()
{
	new i = 64;

	// Game messages start at 64 and go up to ...?
	do {

		if (!get_user_msgname(i, g_msg_name[i], 31))
			break; // Reached last registered message.

	} while (i++ < 255);

	// Engine messages.
	g_msg_name[0] = "SVC_BAD";
	g_msg_name[1] = "SVC_NOP";
	g_msg_name[2] = "SVC_DISCONNECT";
	g_msg_name[3] = "SVC_EVENT";
	g_msg_name[4] = "SVC_VERSION";
	g_msg_name[5] = "SVC_SETVIEW";
	g_msg_name[6] = "SVC_SOUND";
	g_msg_name[7] = "SVC_TIME";
	g_msg_name[8] = "SVC_PRINT";
	g_msg_name[9] = "SVC_STUFFTEXT";
	g_msg_name[10] = "SVC_SETANGLE";
	g_msg_name[11] = "SVC_SERVERINFO";
	g_msg_name[12] = "SVC_LIGHTSTYLE";
	g_msg_name[13] = "SVC_UPDATEUSERINFO";
	g_msg_name[14] = "SVC_DELTADESCRIPTION";
	g_msg_name[15] = "SVC_CLIENTDATA";
	g_msg_name[16] = "SVC_STOPSOUND";
	g_msg_name[17] = "SVC_PINGS";
	g_msg_name[18] = "SVC_PARTICLE";
	g_msg_name[19] = "SVC_DAMAGE";
	g_msg_name[20] = "SVC_SPAWNSTATIC";
	g_msg_name[21] = "SVC_EVENT_RELIABLE";
	g_msg_name[22] = "SVC_SPAWNBASELINE";
	g_msg_name[23] = "SVC_TEMPENTITY";
	g_msg_name[24] = "SVC_SETPAUSE";
	g_msg_name[25] = "SVC_SIGNONNUM";
	g_msg_name[26] = "SVC_CENTERPRINT";
	g_msg_name[27] = "SVC_KILLEDMONSTER";
	g_msg_name[28] = "SVC_FOUNDSECRET";
	g_msg_name[29] = "SVC_SPAWNSTATICSOUND";
	g_msg_name[30] = "SVC_INTERMISSION";
	g_msg_name[31] = "SVC_FINALE";
	g_msg_name[32] = "SVC_CDTRACK";
	g_msg_name[33] = "SVC_RESTORE";
	g_msg_name[34] = "SVC_CUTSCENE";
	g_msg_name[35] = "SVC_WEAPONANIM";
	g_msg_name[36] = "SVC_DECALNAME";
	g_msg_name[37] = "SVC_ROOMTYPE";
	g_msg_name[38] = "SVC_ADDANGLE";
	g_msg_name[39] = "SVC_NEWUSERMSG";
	g_msg_name[40] = "SVC_PACKETENTITIES";
	g_msg_name[41] = "SVC_DELTAPACKETENTITIES";
	g_msg_name[42] = "SVC_CHOKE";
	g_msg_name[43] = "SVC_RESOURCELIST";
	g_msg_name[44] = "SVC_NEWMOVEVARS";
	g_msg_name[45] = "SVC_RESOURCEREQUEST";
	g_msg_name[46] = "SVC_CUSTOMIZATION";
	g_msg_name[47] = "SVC_CROSSHAIRANGLE";
	g_msg_name[48] = "SVC_SOUNDFADE";
	g_msg_name[49] = "SVC_FILETXFERFAILED";
	g_msg_name[50] = "SVC_HLTV";
	g_msg_name[51] = "SVC_DIRECTOR";
	g_msg_name[52] = "SVC_VOICEINIT";
	g_msg_name[53] = "SVC_VOICEDATA";
	g_msg_name[54] = "SVC_SENDEXTRAINFO";
	g_msg_name[55] = "SVC_TIMESCALE";
	g_msg_name[56] = "SVC_RESOURCELOCATION";
	g_msg_name[57] = "SVC_SENDCVARVALUE";
	g_msg_name[58] = "SVC_SENDCVARVALUE2";
	
	// Number of messages.
	return i - 1;
}

#endif
