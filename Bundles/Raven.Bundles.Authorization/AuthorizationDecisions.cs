using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using Newtonsoft.Json.Linq;
using Raven.Bundles.Authorization.Model;
using Raven.Database;
using Raven.Database.Json;
using System.Linq;

namespace Raven.Bundles.Authorization
{
	public class AuthorizationDecisions
	{
		private const string CachePrefix = "Raven.Bundles.Authorization.AuthorizationDecisions.CachePrefix";
		public const string RavenDocumentAuthorization = "Raven-Document-Authorization";

		private readonly DocumentDatabase database;

		public AuthorizationDecisions(DocumentDatabase database)
		{
			this.database = database;
		}

		public bool IsAllowed(
			string userId,
			string operation,
			string documentId, 
			JObject documentMetadata, 
			Action<string> logger)
		{
			var authAsJson = documentMetadata.Value<JObject>(RavenDocumentAuthorization);
			if(authAsJson == null)
			{
				if (logger != null)
					logger("Document " + documentId + " is not secured");
				return true;
			}
			var documentAuthorization = authAsJson.JsonDeserialization<DocumentAuthorization>();
			var user = GetDocumentAsEntityWithCaching<AuthorizationUser>(userId);
			if(user == null)
			{
				if (logger != null) 
					logger("Could not find user: " + userId + " for secured document: " + documentId);
				return false;
			}
			IEnumerable<IPermission> permissions =
				from permission in documentAuthorization.Permissions // permissions for user on document
				where permission.User == userId
				where OperationMatches(permission.Operation, operation)
				select permission;

			permissions.Concat( // permissions on user matching the document's tags
				from tag in documentAuthorization.Tags
				from permission in user.Permissions
				where TagMatches(permission.Tag, tag)
				select permission
				);

			permissions.Concat( // permissions on all user's roles with tags matching the document
				from roleName in GetHierarchicalNames(user.Roles)
				let role = GetDocumentAsEntityWithCaching<AuthorizationRole>(roleName)
				from permission in role.Permissions
				where OperationMatches(permission.Operation, operation)
				from tag in documentAuthorization.Tags
				where TagMatches(permission.Tag, tag)
				select permission
				);

			IEnumerable<IPermission> orderedPermissions = permissions.OrderByDescending(x => x.Priority).ThenBy(x=>x.Allow);
			if(logger != null)
			{
				var list = orderedPermissions.ToList(); // avoid iterating twice on the list
				orderedPermissions = list;
				foreach (var permission in list)
				{
					logger(permission.Explain);
				}
			}
			var decidingPermission = orderedPermissions
				.FirstOrDefault();

			if(decidingPermission == null)
			{
				if (logger != null)
					logger("Could not find any permission for operation: " + operation + " on " + documentId);
				return false;
			}

			return decidingPermission.Allow;
		}

		private static string GetParentName(string operationName)
		{
			int lastIndex = operationName.LastIndexOf('/');
			return operationName.Substring(0, lastIndex);
		}

		private static IEnumerable<string> GetHierarchicalNames(IEnumerable<string> names)
		{
			var hierarchicalNames = new HashSet<string>();
			foreach (var name in names)
			{
				var copy = name;
				do
				{
					hierarchicalNames.Add(copy);
					copy = GetParentName(copy);
				} while (copy != "");
			}
			return hierarchicalNames;
		}
		private static bool OperationMatches(string op1, string op2)
		{
			return op2.StartsWith(op1);
		}

		private static bool TagMatches(string tag1, string tag2)
		{
			return tag2.StartsWith(tag1);
		}

		private T GetDocumentAsEntityWithCaching<T>(string userId)
		{
			var cacheKey = CachePrefix + userId;
			var cachedUser = HttpRuntime.Cache[cacheKey];
			if (cachedUser != null)
				return ((T) cachedUser);

			var userDocument = database.Get(userId, null);
			var user = userDocument.DataAsJson.JsonDeserialization<T>();
			HttpRuntime.Cache[cacheKey] = user;
			return user;
		}
	}
}