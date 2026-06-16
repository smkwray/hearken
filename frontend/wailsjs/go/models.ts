export namespace main {
	
	export class Config {
	    peerIP: string;
	    role: string;
	    direction: string;
	    sndBufKB: number;
	    captureMs: number;
	    recvBufKB: number;
	    playoutMs: number;
	    volumePct: number;
	    autoStart: boolean;
	
	    static createFrom(source: any = {}) {
	        return new Config(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.peerIP = source["peerIP"];
	        this.role = source["role"];
	        this.direction = source["direction"];
	        this.sndBufKB = source["sndBufKB"];
	        this.captureMs = source["captureMs"];
	        this.recvBufKB = source["recvBufKB"];
	        this.playoutMs = source["playoutMs"];
	        this.volumePct = source["volumePct"];
	        this.autoStart = source["autoStart"];
	    }
	}
	export class PeerInfo {
	    ip: string;
	    name: string;
	    os: string;
	
	    static createFrom(source: any = {}) {
	        return new PeerInfo(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.ip = source["ip"];
	        this.name = source["name"];
	        this.os = source["os"];
	    }
	}
	export class Status {
	    os: string;
	    self: string;
	    peer: string;
	    peerIP: string;
	    active: boolean;
	    blackHole: boolean;
	    bridgeOut: boolean;
	    hearUp: boolean;
	    talkUp: boolean;
	    peerConnected: boolean;
	    pingMs: number;
	    direction: string;
	    sndBufKB: number;
	    captureMs: number;
	    recvBufKB: number;
	    playoutMs: number;
	    volumePct: number;
	    autoStart: boolean;
	    missingDeps: string[];
	    note: string;
	    role: string;
	    roleMode: string;
	    selfTailscaleIP: string;
	    selfLANIP: string;
	
	    static createFrom(source: any = {}) {
	        return new Status(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.os = source["os"];
	        this.self = source["self"];
	        this.peer = source["peer"];
	        this.peerIP = source["peerIP"];
	        this.active = source["active"];
	        this.blackHole = source["blackHole"];
	        this.bridgeOut = source["bridgeOut"];
	        this.hearUp = source["hearUp"];
	        this.talkUp = source["talkUp"];
	        this.peerConnected = source["peerConnected"];
	        this.pingMs = source["pingMs"];
	        this.direction = source["direction"];
	        this.sndBufKB = source["sndBufKB"];
	        this.captureMs = source["captureMs"];
	        this.recvBufKB = source["recvBufKB"];
	        this.playoutMs = source["playoutMs"];
	        this.volumePct = source["volumePct"];
	        this.autoStart = source["autoStart"];
	        this.missingDeps = source["missingDeps"];
	        this.note = source["note"];
	        this.role = source["role"];
	        this.roleMode = source["roleMode"];
	        this.selfTailscaleIP = source["selfTailscaleIP"];
	        this.selfLANIP = source["selfLANIP"];
	    }
	}

}

