using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Linq;
using System.Web.Mvc;
using RavenDbBlog.Core.Models;
using RavenDbBlog.DataServices;
using RavenDbBlog.Helpers.Validation;
using RavenDbBlog.Infrastructure.AutoMapper;
using RavenDbBlog.ViewModels;
using System.Web;
using RavenDbBlog.Commands;
using RavenDbBlog.Infrastructure.Commands;

namespace RavenDbBlog.Controllers
{
    public partial class PostController
    {
        public const string CommenterCookieName = "commenter";

        public ActionResult Details(int id, string slug)
        {
            var post = Session
                .Include<Post>(x => x.CommentsId)
                .Load(id);

            if (post == null)
                return HttpNotFound();

            if (post.IsPublicPost() == false)
            {
                Guid guid;
                if (Guid.TryParse(Request.QueryString["key"], out guid) == false || guid != post.ShowPostEvenIfPrivate)
                    return HttpNotFound();
            }

            var vm = new PostViewModel
            {
                Post = post.MapTo<PostViewModel.PostDetails>(),
            };

            if (vm.Post.Slug != slug)
                return RedirectToActionPermanent("Details", new { id, vm.Post.Slug });

            var comments = Session.Load<PostComments>(post.CommentsId);
            vm.Comments = comments.Comments
                .OrderBy(comment => comment.CreatedAt)
                .MapTo<PostViewModel.Comment>();
            vm.NextPost = new PostService(Session).GetPostReference(x => x.PublishAt > post.PublishAt);
            vm.PreviousPost = new PostService(Session).GetPostReference(x => x.PublishAt < post.PublishAt);
            vm.IsCommentClosed = DateTimeOffset.Now - new PostService(Session).GetLastCommentDateForPost(id) > TimeSpan.FromDays(30D);

            var cookie = Request.Cookies[CommenterCookieName];
            if (Request.IsAuthenticated)
            {
                var user = new UserService(Session).GetCurrentUser();
                vm.Input = user.MapTo<CommentInput>();
                vm.IsTrustedCommenter = true;
            }
            else if (cookie != null)
            {
                var commenter = GetCommenter(cookie.Value);
                vm.Input = commenter.MapTo<CommentInput>();
                vm.IsTrustedCommenter = commenter.IsTrustedCommenter == true;
            }
            
            return View(vm);
        }

        [HttpPost]
        public ActionResult Comment(CommentInput input, int id)
        {
            bool isCommentClosed = DateTimeOffset.Now - new PostService(Session).GetLastCommentDateForPost(id) > TimeSpan.FromDays(30D);
            if (isCommentClosed)
            {
                ModelState.AddModelError("CommentClosed", "This post is closed for comments.");
            }

            var commenter = GetCommenter(input.CommenterKey) ?? new Commenter();
            bool isCaptchaRequired = commenter.IsTrustedCommenter != true && Request.IsAuthenticated == false;
            if (isCaptchaRequired)
            {
                var isCaptchaValid = RecaptchaValidatorWrapper.Validate(ControllerContext.HttpContext);
                if (isCaptchaValid == false)
                {
                    ModelState.AddModelError("CaptchaNotValid", "You did not type the verification word correctly. Please try again.");
                }
            }

            var post = Session
                .Include<Post>(x => x.CommentsId)
                .Load(id);

            if (post == null || post.IsPublicPost() == false)
                return HttpNotFound();

            var comments = Session.Load<PostComments>(id);

            if (ModelState.IsValid == false)
            {
                var vm = new PostViewModel
                {
                    Post = post.MapTo<PostViewModel.PostDetails>(),
                    Comments = comments != null ? comments.Comments.MapTo<PostViewModel.Comment>() : new List<PostViewModel.Comment>(),
                    Input = input,
                };

                if (Request.IsAjaxRequest())
                    return Json(new { Success = false, message = ModelState.Values });

                return View("Details", vm);
            }

            CommandExcucator.ExcuteLater(new AddCommentCommand(input, Request.MapTo<RequestValues>(), id));

            Response.Cookies.Add(new HttpCookie(CommenterCookieName, commenter.Key.ToString()));

            const string successMessage = "You feedback will be posted soon. Thanks!";
            if (Request.IsAjaxRequest())
                return Json(new { Success = true, message = successMessage });

            TempData["message"] = successMessage;
            var postReference = post.MapTo<PostReference>();
            return RedirectToAction("Details", new { postReference.Id, postReference.Slug });
        }

        private Commenter GetCommenter(string commenterKey)
        {
            Guid guid;
            if (Guid.TryParse(commenterKey, out guid) == false)
                return null;
            return Session.Query<Commenter>()
                        .Where(x => x.Key == guid)
                        .FirstOrDefault();
        }
    }
}