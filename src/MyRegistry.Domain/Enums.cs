namespace MyRegistry.Domain;

public enum ArtifactType { McpServer, Skill, Agent, Prompt, Plugin, Hook }

public enum VersionStatus { Pending, Approved, ToReview, Deprecated }

public enum Role { Reader, Publisher, Moderator, SecurityValidator, Admin }
