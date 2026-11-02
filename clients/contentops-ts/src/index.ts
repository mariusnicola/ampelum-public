// ContentOps TypeScript Client — generated from C# contracts

export interface TopicRequest {
  candidates: string[];
  brand: string;
  maxTopics?: number;
}

export interface TopicResponse {
  topics: { topic: string; relevanceScore: number; engagementPrediction: number; rationale: string }[];
}

export interface ScriptRequest {
  topic: string;
  channel: string;
  brand: string;
}

export interface ScriptResponse {
  hook: string;
  segments: { text: string; durationSeconds: number }[];
  callToAction: string;
}

export interface StoryboardRequest {
  scriptText: string;
  templateName: string;
  durationSeconds: number;
}

export interface StoryboardResponse {
  shots: { sequenceNumber: number; description: string; cameraAngle: string; durationSeconds: number; props: string[] }[];
}

export interface AdaptChannelRequest {
  scriptText: string;
  channel: string;
  brand: string;
}

export interface AdaptChannelResponse {
  adaptedScript: string;
  warnings: { message: string; severity: string }[];
}

export interface VoiceSelectionRequest {
  scriptText: string;
  channel: string;
  brand: string;
}

export interface VoiceSelectionResponse {
  selectedVoiceTalent: string;
  confidence: number;
}

export interface SafetyRequest {
  content: string;
  channel: string;
  brand: string;
}

export interface SafetyResponse {
  flags: { category: string; message: string; severity: string }[];
}

export class ContentOpsClient {
  constructor(private baseUrl: string = "http://localhost:5000") {}

  private async post<TReq, TRes>(path: string, body: TReq): Promise<TRes> {
    const res = await fetch(`${this.baseUrl}/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      throw new Error(`ContentOps API error: ${res.status} ${res.statusText}`);
    }
    return res.json() as Promise<TRes>;
  }

  async rankTopics(req: TopicRequest): Promise<TopicResponse> {
    return this.post("contentops/rank-topics", req);
  }

  async generateScript(req: ScriptRequest): Promise<ScriptResponse> {
    return this.post("contentops/generate-script", req);
  }

  async generateStoryboard(req: StoryboardRequest): Promise<StoryboardResponse> {
    return this.post("contentops/generate-storyboard", req);
  }

  async adaptForChannel(req: AdaptChannelRequest): Promise<AdaptChannelResponse> {
    return this.post("contentops/adapt-channel", req);
  }

  async selectVoice(req: VoiceSelectionRequest): Promise<VoiceSelectionResponse> {
    return this.post("contentops/select-voice", req);
  }

  async checkSafety(req: SafetyRequest): Promise<SafetyResponse> {
    return this.post("contentops/check-safety", req);
  }
}